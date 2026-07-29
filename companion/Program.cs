using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace LoFiRoom.Companion;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var app = new CompanionContext();
        Application.Run(app);
    }
}

internal sealed class CompanionContext : ApplicationContext
{
    private const string ClientId = "1531990024122532003";
    private readonly NotifyIcon _tray;
    private readonly DiscordIpcClient _discord = new(ClientId);
    private readonly HttpListener _listener = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new();
    private readonly CancellationTokenSource _cts = new();
    private PresenceRequest _current = PresenceRequest.Awake();
    private bool _awayApplied;

    public CompanionContext()
    {
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Lo-fi Room",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _listener.Prefixes.Add("http://127.0.0.1:47372/");
        _listener.Prefixes.Add("http://localhost:47372/");
        _listener.Start();
        _ = Task.Run(ListenAsync);

        _idleTimer.Interval = 5000;
        _idleTimer.Tick += (_, _) => CheckIdleState();
        _idleTimer.Start();

        _ = ApplyPresenceAsync(_current);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Awake", null, async (_, _) => await ActivateAsync(PresenceRequest.Awake()));
        menu.Items.Add("Busy", null, async (_, _) => await ActivateAsync(PresenceRequest.Busy()));
        menu.Items.Add("Launch with Windows", null, (_, _) => ToggleStartup());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(1000, _cts.Token);
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        AddCors(context.Response);

        if (context.Request.HttpMethod == "OPTIONS")
        {
            context.Response.StatusCode = 204;
            context.Response.Close();
            return;
        }

        if (context.Request.Url?.AbsolutePath == "/health")
        {
            await WriteJsonAsync(context.Response, new { ok = true, connected = _discord.IsConnected });
            return;
        }

        if (context.Request.Url?.AbsolutePath != "/presence" || context.Request.HttpMethod != "POST")
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<PresenceRequest>(
                body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );

            if (request?.Preset is null)
            {
                context.Response.StatusCode = 400;
                await WriteJsonAsync(context.Response, new { ok = false, error = "Missing preset" });
                return;
            }

            await ActivateAsync(request);
            await WriteJsonAsync(context.Response, new { ok = true, connected = _discord.IsConnected });
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { ok = false, error = ex.Message });
        }
    }

    private async Task ActivateAsync(PresenceRequest request)
    {
        _current = request with { StartedAt = request.StartedAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : request.StartedAt };
        SetStartup(_current.LaunchWithWindows);
        _awayApplied = request.Preset?.Id == "away";
        await ApplyPresenceAsync(_current);
    }

    private async Task ApplyPresenceAsync(PresenceRequest request)
    {
        if (request.Preset is null) return;

        var activity = new DiscordActivity
        {
            Name = request.Preset.Playing,
            Details = request.Preset.Details,
            State = request.Preset.State,
            Timestamps = request.ElapsedEnabled ? new DiscordTimestamps { Start = request.StartedAt } : null,
            Assets = new DiscordAssets
            {
                LargeImage = BuildArtworkKey(request.Preset.ArtworkKey, request.TimeOfDay),
                LargeText = request.Preset.Label,
            },
            Buttons = string.IsNullOrWhiteSpace(request.Preset.ButtonUrl)
                ? null
                : new[]
                {
                    new DiscordButton
                    {
                        Label = string.IsNullOrWhiteSpace(request.Preset.ButtonLabel) ? "Open" : request.Preset.ButtonLabel,
                        Url = request.Preset.ButtonUrl,
                    },
                },
        };

        await _discord.SetActivityAsync(activity);
        _tray.Text = TrimTrayText($"Lo-fi Room - {request.Preset.Label}");
    }

    private void CheckIdleState()
    {
        if (_current.Preset is null) return;

        var currentPart = PresenceRequest.CurrentTimeOfDay();
        if (!string.Equals(_current.TimeOfDay, currentPart, StringComparison.OrdinalIgnoreCase))
        {
            _current = _current with { TimeOfDay = currentPart };
            _ = ApplyPresenceAsync(_current);
        }

        if (!_current.AutoAway || _current.Preset.Id == "busy") return;

        var idle = IdleSeconds();
        var awayAfter = Math.Max(1, _current.AutoAwayMinutes) * 60;

        if (!_awayApplied && _current.Preset.Id == "awake" && idle >= awayAfter)
        {
            _awayApplied = true;
            _ = ApplyPresenceAsync(_current with
            {
                Preset = new PresetPayload
                {
                    Id = "away",
                    Label = "Away",
                    Playing = "Lo-fi Room",
                    Details = "Stepped away for a bit",
                    State = "Back soon",
                    ArtworkKey = _current.Preset.ArtworkKey,
                },
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
        else if (_awayApplied && idle < 3)
        {
            _awayApplied = false;
            _ = ApplyPresenceAsync(_current with
            {
                Preset = _current.Preset with
                {
                    Id = "awake",
                    Label = "Awake",
                    Details = "Awake and caffeinating",
                    State = "Coffee brewed",
                },
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
        }
    }

    private static string BuildArtworkKey(string? baseKey, string? timeOfDay)
    {
        var key = string.IsNullOrWhiteSpace(baseKey) ? "lofi-bedroom" : baseKey;
        var suffix = string.IsNullOrWhiteSpace(timeOfDay) ? "morning" : timeOfDay;
        return $"{key}-{suffix}".ToLowerInvariant();
    }

    private static string TrimTrayText(string text) => text.Length <= 63 ? text : text[..63];

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        AddCors(response);
        response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static void AddCors(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Headers"] = "content-type";
        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
    }

    private static void ToggleStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        var current = key?.GetValue("LoFiRoomCompanion") as string;
        SetStartup(string.IsNullOrWhiteSpace(current));
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (enabled)
        {
            key?.SetValue("LoFiRoomCompanion", $"\"{Application.ExecutablePath}\"");
        }
        else
        {
            key?.DeleteValue("LoFiRoomCompanion", false);
        }
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _idleTimer.Stop();
        _listener.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private static uint IdleSeconds()
    {
        var lastInput = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref lastInput) ? ((uint)Environment.TickCount - lastInput.DwTime) / 1000 : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }
}

internal sealed class DiscordIpcClient(string clientId)
{
    private NamedPipeClientStream? _pipe;
    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task SetActivityAsync(DiscordActivity activity)
    {
        await EnsureConnectedAsync();
        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString("N"),
        };
        await SendAsync(1, payload);
    }

    private async Task EnsureConnectedAsync()
    {
        if (IsConnected) return;

        for (var i = 0; i < 10; i++)
        {
            try
            {
                _pipe?.Dispose();
                _pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(1000);
                await SendAsync(0, new { v = 1, client_id = clientId });
                _ = Task.Run(ReadLoopAsync);
                return;
            }
            catch
            {
                _pipe?.Dispose();
                _pipe = null;
            }
        }
    }

    private async Task SendAsync(int opCode, object payload)
    {
        if (_pipe is null || !_pipe.IsConnected) return;
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), opCode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), body.Length);
        await _pipe.WriteAsync(header);
        await _pipe.WriteAsync(body);
        await _pipe.FlushAsync();
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[8];
        while (_pipe?.IsConnected == true)
        {
            try
            {
                var read = await _pipe.ReadAsync(header);
                if (read == 0) break;
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
                var buffer = new byte[length];
                await _pipe.ReadExactlyAsync(buffer);
            }
            catch
            {
                break;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record PresenceRequest
{
    public PresetPayload? Preset { get; init; }
    public long StartedAt { get; init; }
    public bool AutoAway { get; init; } = true;
    public int AutoAwayMinutes { get; init; } = 5;
    public bool ElapsedEnabled { get; init; } = true;
    public bool LaunchWithWindows { get; init; } = true;
    public bool StartMinimized { get; init; } = true;
    public string? TimeOfDay { get; init; } = "morning";

    public static PresenceRequest Awake() => new()
    {
        Preset = new PresetPayload
        {
            Id = "awake",
            Label = "Awake",
            Playing = "Lo-fi Room",
            Details = "Awake and caffeinating",
            State = "Coffee brewed",
            ArtworkKey = "lofi-bedroom",
        },
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        TimeOfDay = CurrentTimeOfDay(),
    };

    public static PresenceRequest Busy() => new()
    {
        Preset = new PresetPayload
        {
            Id = "busy",
            Label = "Busy",
            Playing = "Lo-fi Room",
            Details = "Focus mode activated",
            State = "Headphones on",
            ArtworkKey = "lofi-bedroom",
        },
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        TimeOfDay = CurrentTimeOfDay(),
    };

    public static string CurrentTimeOfDay()
    {
        var hour = DateTime.Now.Hour;
        if (hour is >= 6 and < 12) return "morning";
        if (hour is >= 12 and < 17) return "afternoon";
        if (hour is >= 17 and < 21) return "evening";
        return "night";
    }
}

internal sealed record PresetPayload
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Playing { get; init; } = "";
    public string Details { get; init; } = "";
    public string State { get; init; } = "";
    public string ArtworkKey { get; init; } = "";
    public string? ButtonLabel { get; init; }
    public string? ButtonUrl { get; init; }
}

internal sealed record DiscordActivity
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("details")]
    public string? Details { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("timestamps")]
    public DiscordTimestamps? Timestamps { get; init; }

    [JsonPropertyName("assets")]
    public DiscordAssets? Assets { get; init; }

    [JsonPropertyName("buttons")]
    public DiscordButton[]? Buttons { get; init; }
}

internal sealed record DiscordTimestamps
{
    [JsonPropertyName("start")]
    public long Start { get; init; }
}

internal sealed record DiscordAssets
{
    [JsonPropertyName("large_image")]
    public string? LargeImage { get; init; }

    [JsonPropertyName("large_text")]
    public string? LargeText { get; init; }
}

internal sealed record DiscordButton
{
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
