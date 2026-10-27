using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipSync.Windows.Crypto;
using ClipSync.Windows.Models;

namespace ClipSync.Windows.Services;

public sealed class WebSocketSyncService
{
    private readonly string _serverUrl;
    private readonly string _deviceId;
    private readonly string _token;
    private readonly ClipSyncCrypto _crypto;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private readonly HashSet<string> _seenItemIds = new();
    private int _lastSequence = 0;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsPaused { get; set; } = false;

    public event Action<string>? OnStateChanged;
    public event Action<SyncItem, string>? OnRemoteItemReceived;
    public event Action<string>? OnStatusMessage;

    // Phase 2C: Large File & Image Transfer Events
    public event Action<TransferManifest>? OnTransferAnnounced;
    public event Action<string, string, long, long, string>? OnTransferProgressRelay; // tid, dev, bytes, total, phase
    public event Action<string, string, bool, string?, bool>? OnTransferDelivered; // tid, recipient, allDelivered, driveFileId, shouldDelete
    public event Action<string, string?>? OnTransferExpired; // tid, driveFileId
    public event Action<string, string?>? OnTransferDeleted; // tid, driveFileId

    public WebSocketSyncService(string serverUrl, string deviceId, string token, ClipSyncCrypto crypto)
    {
        _serverUrl = serverUrl;
        _deviceId = deviceId;
        _token = token;
        _crypto = crypto;
    }

    public void Start()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = ConnectLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try
        {
            _webSocket?.Dispose();
        }
        catch { }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                OnStateChanged?.Invoke("Connecting...");
                _webSocket = new ClientWebSocket();

                string wsScheme = _serverUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws";
                string cleanHost = _serverUrl.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                                             .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                                             .TrimEnd('/');

                var uri = new Uri($"{wsScheme}://{cleanHost}/v1/events?token={Uri.EscapeDataString(_token)}");
                await _webSocket.ConnectAsync(uri, ct);

                attempt = 0;
                OnStateChanged?.Invoke(IsPaused ? "Paused" : "Connected");

                _ = HeartbeatLoopAsync(_webSocket, ct);
                await ReceiveLoopAsync(_webSocket, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                OnStateChanged?.Invoke("Disconnected");
                int delayMs = Math.Min((int)(1000 * Math.Pow(1.8, attempt)), 15000);
                attempt++;
                await Task.Delay(delayMs, ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                break;
            }

            string jsonStr = Encoding.UTF8.GetString(buffer, 0, result.Count);
            HandleMessage(jsonStr, ws, ct);
        }
    }

    private void HandleMessage(string jsonStr, ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;

            string type = typeProp.GetString() ?? "";

            switch (type)
            {
                case "connected":
                    if (root.TryGetProperty("lastSequence", out var lastSeq))
                    {
                        _lastSequence = lastSeq.GetInt32();
                    }
                    OnStatusMessage?.Invoke("Connected to ClipSync");
                    break;

                case "sync:item":
                    if (!root.TryGetProperty("item", out var itemProp)) return;
                    int seq = root.TryGetProperty("sequenceNumber", out var sProp) ? sProp.GetInt32() : 0;
                    _lastSequence = Math.Max(_lastSequence, seq);

                    string itemId = itemProp.GetProperty("itemId").GetString() ?? "";
                    string sourceId = itemProp.GetProperty("sourceDeviceId").GetString() ?? "";
                    string payload = itemProp.GetProperty("payload").GetString() ?? "";

                    // Send Ack
                    _ = SendAckAsync(ws, itemId, seq, ct);

                    // Ignore self echo
                    if (sourceId == _deviceId) return;

                    // Deduplicate
                    if (_seenItemIds.Contains(itemId)) return;
                    _seenItemIds.Add(itemId);

                    if (IsPaused) return;

                    // Decrypt
                    var envelope = JsonSerializer.Deserialize<EncryptedPayloadEnvelope>(payload);
                    if (envelope != null)
                    {
                        string plainText = _crypto.Decrypt(envelope);
                        var item = JsonSerializer.Deserialize<SyncItem>(itemProp.GetRawText());
                        if (item != null)
                        {
                            OnRemoteItemReceived?.Invoke(item, plainText);
                        }
                    }
                    break;

                case "device:joined":
                    OnStatusMessage?.Invoke("Device joined");
                    break;

                case "transfer:announced":
                    if (root.TryGetProperty("manifest", out var manifestProp))
                    {
                        var manifest = JsonSerializer.Deserialize<TransferManifest>(manifestProp.GetRawText());
                        if (manifest != null)
                        {
                            OnTransferAnnounced?.Invoke(manifest);
                        }
                    }
                    break;

                case "transfer:progress_relay":
                    string pTid = root.TryGetProperty("transferId", out var tidP) ? tidP.GetString() ?? "" : "";
                    string pDev = root.TryGetProperty("deviceId", out var devP) ? devP.GetString() ?? "" : "";
                    long pBytes = root.TryGetProperty("bytesTransferred", out var bP) ? bP.GetInt64() : 0;
                    long pTotal = root.TryGetProperty("totalBytes", out var tP) ? tP.GetInt64() : 0;
                    string pPhase = root.TryGetProperty("phase", out var phP) ? phP.GetString() ?? "" : "";
                    OnTransferProgressRelay?.Invoke(pTid, pDev, pBytes, pTotal, pPhase);
                    break;

                case "transfer:delivered":
                    string dltTid = root.TryGetProperty("transferId", out var dtP) ? dtP.GetString() ?? "" : "";
                    string dltRec = root.TryGetProperty("recipientDeviceId", out var drP) ? drP.GetString() ?? "" : "";
                    bool allDel = root.TryGetProperty("allDelivered", out var adP) && adP.GetBoolean();
                    string? dltDfid = root.TryGetProperty("driveFileId", out var dfP) ? dfP.GetString() : null;
                    bool shouldDel = root.TryGetProperty("shouldDelete", out var sdP) && sdP.GetBoolean();
                    OnTransferDelivered?.Invoke(dltTid, dltRec, allDel, dltDfid, shouldDel);
                    break;

                case "transfer:expired":
                    string expTid = root.TryGetProperty("transferId", out var etP) ? etP.GetString() ?? "" : "";
                    string? expDfid = root.TryGetProperty("driveFileId", out var edfP) ? edfP.GetString() : null;
                    OnTransferExpired?.Invoke(expTid, expDfid);
                    break;

                case "transfer:deleted":
                    string delTid = root.TryGetProperty("transferId", out var delP) ? delP.GetString() ?? "" : "";
                    string? delDfid = root.TryGetProperty("driveFileId", out var deldfP) ? deldfP.GetString() : null;
                    OnTransferDeleted?.Invoke(delTid, delDfid);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling frame: {ex.Message}");
        }
    }

    public async Task BroadcastTextAsync(string text)
    {
        if (IsPaused || _webSocket == null || _webSocket.State != WebSocketState.Open) return;

        try
        {
            var envelope = _crypto.Encrypt(text);
            string envelopeJson = JsonSerializer.Serialize(envelope);
            string itemId = $"win-{Guid.NewGuid():N}";

            _seenItemIds.Add(itemId);

            var item = new SyncItem(
                Version: 1,
                ItemId: itemId,
                Type: "text",
                SourceDeviceId: _deviceId,
                CreatedAt: DateTime.UtcNow.ToString("O"),
                Payload: envelopeJson,
                Metadata: new SyncItemMetadata(
                    Size: Encoding.UTF8.GetByteCount(text),
                    Sha256: ClipSyncCrypto.ComputeSha256(text),
                    PreviewSnippet: text.Length > 100 ? text[..100] : text,
                    SourceApp: "Windows PC"
                )
            );

            var messageObj = new
            {
                type = "sync:item",
                item
            };

            string json = JsonSerializer.Serialize(messageObj);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Broadcast error: {ex.Message}");
        }
    }

    private async Task SendAckAsync(ClientWebSocket ws, string itemId, int sequenceNumber, CancellationToken ct)
    {
        try
        {
            var ack = new { type = "ack", itemId, sequenceNumber };
            string json = JsonSerializer.Serialize(ack);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        catch { }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            await Task.Delay(20000, ct);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, ct);
            }
            catch { break; }
        }
    }

    public async Task SendTransferInitAsync(TransferManifest manifest)
    {
        if (IsPaused || _webSocket == null || _webSocket.State != WebSocketState.Open) return;
        try
        {
            var msg = new { type = "transfer:init", manifest };
            string json = JsonSerializer.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] SendTransferInit error: {ex.Message}");
        }
    }

    public async Task SendTransferAckAsync(string transferId)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;
        try
        {
            var msg = new { type = "transfer:ack", transferId, recipientDeviceId = _deviceId };
            string json = JsonSerializer.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] SendTransferAck error: {ex.Message}");
        }
    }

    public async Task SendTransferProgressAsync(string transferId, long bytesTransferred, long totalBytes, string phase)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;
        try
        {
            var msg = new { type = "transfer:progress", transferId, bytesTransferred, totalBytes, phase };
            string json = JsonSerializer.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    public async Task SendTransferCancelAsync(string transferId, string? reason = null)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;
        try
        {
            var msg = new { type = "transfer:cancel", transferId, reason = reason ?? "Cancelled by user" };
            string json = JsonSerializer.Serialize(msg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }
}
