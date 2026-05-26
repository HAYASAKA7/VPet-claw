using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VPet.Plugin.OpenClawBridge.Models;

namespace VPet.Plugin.OpenClawBridge
{
    public class OpenClawClient
    {
        private ClientWebSocket _ws;
        private readonly string _serverUrl;
        private readonly string _sessionKey;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;
        private bool _gatewayConnected;
        private bool _sessionSubscribed;
        private string _token = "";
        private int _nextId;
        private string? _connectRequestId;
        private string? _sessionSubscribeRequestId;

        public event Action<OpenClawMessage>? OnMessageReceived;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string>? OnError;

        public bool IsConnected => _ws.State == WebSocketState.Open && _gatewayConnected;

        public OpenClawClient(string url = "ws://localhost:3000", string sessionKey = "agent:main:main")
        {
            _serverUrl = url;
            _sessionKey = string.IsNullOrWhiteSpace(sessionKey) ? "agent:main:main" : sessionKey.Trim();
            _ws = new ClientWebSocket();
        }

        public async Task ConnectAsync(string token)
        {
            try
            {
                _token = token;
                if (!Uri.TryCreate(_serverUrl, UriKind.Absolute, out var serverUri) ||
                    (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps &&
                     serverUri.Scheme != Uri.UriSchemeWs && serverUri.Scheme != Uri.UriSchemeWss))
                {
                    OnError?.Invoke($"Invalid WebSocket URL: {_serverUrl}");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(token))
                {
                    _ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                }
                _ws.Options.SetRequestHeader("User-Agent", "VPet.OpenClawBridge/0.1");

                await _ws.ConnectAsync(serverUri, _cts.Token);
                _ = ReceiveLoopAsync();
            }
            catch (OperationCanceledException)
            {
                OnDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenClaw] Connect error: {ex.Message}");
                OnError?.Invoke(ex.Message);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[4096];
            var message = new ArraySegment<byte>(buffer);

            try
            {
                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    using var stream = new System.IO.MemoryStream();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _ws.ReceiveAsync(message, _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await CloseAsync();
                            OnDisconnected?.Invoke();
                            return;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string messageStr = Encoding.UTF8.GetString(stream.ToArray());
                    await DispatchMessageAsync(messageStr);
                }
            }
            catch (OperationCanceledException)
            {
                OnDisconnected?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                OnDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex.Message);
                OnDisconnected?.Invoke();
            }
        }

        private async Task DispatchMessageAsync(string messageStr)
        {
            try
            {
                using var document = JsonDocument.Parse(messageStr);
                var root = document.RootElement;
                if (root.TryGetProperty("type", out var typeElement))
                {
                    await DispatchGatewayFrameAsync(typeElement.GetString(), root);
                    return;
                }

                var msg = JsonSerializer.Deserialize<OpenClawMessage>(messageStr);
                if (msg != null)
                {
                    OnMessageReceived?.Invoke(msg);
                }
            }
            catch (JsonException ex)
            {
                OnError?.Invoke($"Invalid OpenClaw message: {ex.Message}");
            }
        }

        private async Task DispatchGatewayFrameAsync(string? frameType, JsonElement root)
        {
            switch (frameType)
            {
                case "event":
                    if (root.TryGetProperty("event", out var eventElement))
                    {
                        var eventName = eventElement.GetString();
                        if (eventName == "connect.challenge")
                        {
                            await SendGatewayConnectAsync(root);
                            return;
                        }

                        DispatchGatewayEvent(eventName, root);
                    }
                    break;
                case "res":
                    await DispatchGatewayResponseAsync(root);
                    break;
            }
        }

        private async Task DispatchGatewayResponseAsync(JsonElement root)
        {
            bool ok = root.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (ok)
            {
                if (root.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("type", out var payloadType) &&
                    payloadType.GetString() == "hello-ok")
                {
                    _gatewayConnected = true;
                    OnConnected?.Invoke();
                    _ = SubscribeSessionMessagesAsync();
                }

                return;
            }

            var error = ReadResponseError(root);
            if (root.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String &&
                idElement.GetString() == _sessionSubscribeRequestId)
            {
                Console.WriteLine($"[OpenClaw] Session message subscription failed: {error}");
                return;
            }

            OnError?.Invoke(error ?? "OpenClaw gateway request failed.");
        }

        private void DispatchGatewayEvent(string? eventName, JsonElement root)
        {
            switch (eventName)
            {
                case "agent.run.started":
                case "chat.message.started":
                case "session.operation":
                    OnMessageReceived?.Invoke(new OpenClawMessage { Type = "working", RunId = ReadPayloadString(root, "runId") });
                    break;
                case "agent.run.failed":
                case "chat.message.failed":
                    OnMessageReceived?.Invoke(new OpenClawMessage
                    {
                        Type = "error",
                        Payload = ReadPayloadText(root),
                        IsFinal = true,
                        RunId = ReadPayloadString(root, "runId")
                    });
                    break;
                case "agent.run.completed":
                case "chat.message.completed":
                    OnMessageReceived?.Invoke(new OpenClawMessage
                    {
                        Type = "success",
                        IsFinal = true,
                        RunId = ReadPayloadString(root, "runId")
                    });
                    break;
                case "agent":
                    DispatchAgentEvent(root);
                    break;
                case "chat.message.delta":
                case "agent.run.message.delta":
                    var text = ReadPayloadText(root);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        OnMessageReceived?.Invoke(new OpenClawMessage
                        {
                            Type = "reply",
                            Payload = text,
                            IsDelta = true,
                            RunId = ReadPayloadString(root, "runId")
                        });
                    }
                    break;
                case "chat":
                    DispatchChatEvent(root);
                    break;
                case "session.message":
                    DispatchSessionMessageEvent(root);
                    break;
                case "tool.call":
                case "agent.tool.call":
                case "session.tool":
                    var tool = ReadPayloadString(root, "tool") ?? ReadPayloadString(root, "name");
                    var args = ReadPayloadString(root, "args") ?? ReadPayloadString(root, "arguments");
                    OnMessageReceived?.Invoke(new OpenClawMessage { Type = "tool_call", Tool = tool, Args = args });
                    break;
            }
        }

        private void DispatchChatEvent(JsonElement root)
        {
            var state = ReadPayloadString(root, "state");
            var runId = ReadPayloadString(root, "runId");
            switch (state)
            {
                case "delta":
                    var deltaText = ReadMessageContent(root) ?? ReadPayloadString(root, "deltaText");
                    if (!string.IsNullOrWhiteSpace(deltaText))
                    {
                        OnMessageReceived?.Invoke(new OpenClawMessage
                        {
                            Type = "reply",
                            Payload = deltaText,
                            IsDelta = true,
                            Replace = ReadPayloadBool(root, "replace"),
                            RunId = runId
                        });
                    }
                    break;
                case "final":
                    var finalText = ReadMessageContent(root);
                    if (!string.IsNullOrWhiteSpace(finalText))
                    {
                        OnMessageReceived?.Invoke(new OpenClawMessage
                        {
                            Type = "reply",
                            Payload = finalText,
                            IsFinal = true,
                            RunId = runId
                        });
                    }
                    else
                    {
                        OnMessageReceived?.Invoke(new OpenClawMessage { Type = "success", IsFinal = true, RunId = runId });
                    }
                    break;
                case "error":
                    OnMessageReceived?.Invoke(new OpenClawMessage
                    {
                        Type = "error",
                        Payload = ReadPayloadString(root, "errorMessage") ?? ReadPayloadString(root, "error"),
                        IsFinal = true,
                        RunId = runId
                    });
                    break;
            }
        }

        private void DispatchAgentEvent(JsonElement root)
        {
            var stream = ReadPayloadString(root, "stream");
            var runId = ReadPayloadString(root, "runId");
            if (stream == "assistant")
            {
                var text = ReadPayloadString(root, "text") ?? ReadPayloadString(root, "delta");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    OnMessageReceived?.Invoke(new OpenClawMessage
                    {
                        Type = "reply",
                        Payload = text,
                        IsDelta = true,
                        RunId = runId
                    });
                }

                return;
            }

            if (stream == "lifecycle")
            {
                var phase = ReadPayloadString(root, "phase");
                if (phase == "start")
                {
                    OnMessageReceived?.Invoke(new OpenClawMessage { Type = "working", RunId = runId });
                }
                else if (phase == "end")
                {
                    OnMessageReceived?.Invoke(new OpenClawMessage { Type = "success", IsFinal = true, RunId = runId });
                }
                else if (phase == "error")
                {
                    OnMessageReceived?.Invoke(new OpenClawMessage
                    {
                        Type = "error",
                        Payload = ReadPayloadString(root, "error"),
                        IsFinal = true,
                        RunId = runId
                    });
                }
                return;
            }

            OnMessageReceived?.Invoke(new OpenClawMessage { Type = "working", RunId = runId });
        }

        private void DispatchSessionMessageEvent(JsonElement root)
        {
            var role = ReadMessageRole(root);
            if (!string.IsNullOrWhiteSpace(role) && role != "assistant")
            {
                return;
            }

            var text = ReadMessageContent(root);
            if (!string.IsNullOrWhiteSpace(text))
            {
                OnMessageReceived?.Invoke(new OpenClawMessage
                {
                    Type = "reply",
                    Payload = text,
                    IsFinal = true,
                    RunId = ReadPayloadString(root, "runId")
                });
            }
        }

        private async Task SendGatewayConnectAsync(JsonElement challengeFrame)
        {
            _ = challengeFrame;
            _connectRequestId = NextId().ToString();
            var payload = new
            {
                type = "req",
                id = _connectRequestId,
                method = "connect",
                @params = new
                {
                    minProtocol = 4,
                    maxProtocol = 4,
                    client = new
                    {
                        id = "gateway-client",
                        version = "0.1.0",
                        platform = "windows",
                        mode = "backend"
                    },
                    role = "operator",
                    scopes = new[] { "operator.read", "operator.write" },
                    caps = Array.Empty<string>(),
                    commands = Array.Empty<string>(),
                    permissions = new { },
                    auth = new
                    {
                        token = _token
                    },
                    locale = "en-US",
                    userAgent = "VPet.OpenClawBridge/0.1",
                }
            };

            await SendJsonAsync(payload);
        }

        public async Task<bool> SendMessageAsync(string text)
        {
            if (!IsConnected)
            {
                return false;
            }

            var payload = new
            {
                type = "req",
                id = NextId().ToString(),
                method = "chat.send",
                @params = new
                {
                    sessionKey = _sessionKey,
                    message = text,
                    idempotencyKey = Guid.NewGuid().ToString("N")
                }
            };

            await SendJsonAsync(payload);
            return true;
        }

        private async Task SubscribeSessionMessagesAsync()
        {
            if (_sessionSubscribed)
            {
                return;
            }

            _sessionSubscribed = true;
            _sessionSubscribeRequestId = NextId().ToString();
            var payload = new
            {
                type = "req",
                id = _sessionSubscribeRequestId,
                method = "sessions.messages.subscribe",
                @params = new
                {
                    key = _sessionKey
                }
            };

            await SendJsonAsync(payload);
        }

        private async Task SendJsonAsync(object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token);
        }

        private int NextId()
        {
            return Interlocked.Increment(ref _nextId);
        }

        private static string? ReadPayloadText(JsonElement root)
        {
            return ReadMessageContent(root)
                ?? ReadPayloadString(root, "text")
                ?? ReadPayloadString(root, "deltaText")
                ?? ReadPayloadString(root, "message")
                ?? ReadPayloadString(root, "errorMessage")
                ?? ReadPayloadString(root, "error");
        }

        private static string? ReadPayloadString(JsonElement root, string propertyName)
        {
            return ReadStringInContainer(root, propertyName)
                ?? ReadNestedString(root, "payload", propertyName)
                ?? ReadNestedString(root, "params", propertyName)
                ?? ReadNestedPayloadString(root, propertyName)
                ?? ReadPayloadDataString(root, propertyName);
        }

        private static string? ReadMessageRole(JsonElement root)
        {
            if (TryGetPayload(root, out var payload) &&
                payload.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("role", out var role) &&
                role.ValueKind == JsonValueKind.String)
            {
                return role.GetString();
            }

            return null;
        }

        private static string? ReadMessageContent(JsonElement root)
        {
            if (!TryGetPayload(root, out var payload) ||
                !payload.TryGetProperty("message", out var message))
            {
                return null;
            }

            if (message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content))
            {
                return null;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var textPart) &&
                    textPart.ValueKind == JsonValueKind.String)
                {
                    builder.Append(textPart.GetString());
                }
            }

            var text = builder.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static bool ReadPayloadBool(JsonElement root, string propertyName)
        {
            return TryGetPayload(root, out var payload) &&
                payload.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.True;
        }

        private static bool TryGetPayload(JsonElement root, out JsonElement payload)
        {
            if (root.TryGetProperty("payload", out payload))
            {
                return true;
            }

            if (root.TryGetProperty("params", out var parameters))
            {
                if (parameters.TryGetProperty("payload", out payload))
                {
                    return true;
                }

                payload = parameters;
                return true;
            }

            payload = default;
            return false;
        }

        private static string? ReadStringInContainer(JsonElement container, string propertyName)
        {
            if (container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty(propertyName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static string? ReadNestedString(JsonElement root, string containerName, string propertyName)
        {
            if (root.TryGetProperty(containerName, out var container))
            {
                return ReadStringInContainer(container, propertyName);
            }

            return null;
        }

        private static string? ReadNestedPayloadString(JsonElement root, string propertyName)
        {
            if (root.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("payload", out var payload))
            {
                return ReadStringInContainer(payload, propertyName);
            }

            return null;
        }

        private static string? ReadPayloadDataString(JsonElement root, string propertyName)
        {
            if (TryGetPayload(root, out var payload) &&
                payload.TryGetProperty("data", out var data))
            {
                return ReadStringInContainer(data, propertyName);
            }

            return null;
        }

        private static string? ReadResponseError(JsonElement root)
        {
            if (!root.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }

            if (error.TryGetProperty("details", out var details))
            {
                return details.ToString();
            }

            return error.ToString();
        }

        public async Task CloseAsync()
        {
            if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _ws.Dispose();
            _cts.Dispose();
        }
    }
}
