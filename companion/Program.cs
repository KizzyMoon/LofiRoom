using System.Buffers.Binary;
using System.Diagnostics;
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
    private const string FixedPlaying = "Kizzy's Corner";
    private const string RemotePresenceUrl = "https://api.github.com/repos/KizzyMoon/LofiRoom/contents/presets.json?ref=main";
    private readonly NotifyIcon _tray;
    private readonly DiscordIpcClient _discord = new(ClientId);
    private readonly HttpListener _listener = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new();
    private readonly System.Windows.Forms.Timer _remoteTimer = new();
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _cts = new();
    private PresenceRequest _current = PresenceRequest.Chilling();
    private PresenceRequest? _beforeAway;
    private bool _awayApplied;
    private string? _lastRemoteUpdate;
    private string? _lastGamingState;
    private DateTimeOffset _lastLocalActivation = DateTimeOffset.MinValue;

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

        _remoteTimer.Interval = 60000;
        _remoteTimer.Tick += async (_, _) => await CheckRemotePresenceAsync();
        _remoteTimer.Start();

        _ = ApplyPresenceAsync(_current);
        _ = CheckRemotePresenceAsync();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Chilling", null, async (_, _) => await ActivateAsync(PresenceRequest.Chilling()));
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
            await WriteJsonAsync(context.Response, CompanionStatus());
            return;
        }

        if (context.Request.Url?.AbsolutePath == "/force")
        {
            await ForceDiscordRefreshAsync();
            await WriteJsonAsync(context.Response, CompanionStatus());
            return;
        }

        if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath.StartsWith("/preset/", StringComparison.OrdinalIgnoreCase) == true)
        {
            var id = WebUtility.UrlDecode(context.Request.Url.AbsolutePath["/preset/".Length..]).Trim();
            var request = PresenceRequest.FromPresetId(id);
            await ActivateAsync(request, localOverride: true);
            await WriteJsonAsync(context.Response, CompanionStatus());
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

            await ActivateAsync(request, localOverride: true);
            await WriteJsonAsync(context.Response, CompanionStatus());
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context.Response, new { ok = false, error = ex.Message });
        }
    }

    private async Task ActivateAsync(PresenceRequest request, bool localOverride = false)
    {
        if (localOverride)
        {
            _lastLocalActivation = DateTimeOffset.UtcNow;
        }

        request = NormalizeMorningPreset(request);
        _current = request with { StartedAt = request.StartedAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : request.StartedAt };
        SetStartup(_current.LaunchWithWindows);
        _awayApplied = request.Preset?.Id == "away";
        if (!_awayApplied)
        {
            _beforeAway = null;
            _lastGamingState = null;
        }
        await ApplyPresenceAsync(_current);
    }

    private object CompanionStatus() => new
    {
        ok = true,
        connected = _discord.IsConnected,
        preset = _current.Preset?.Id,
        label = _current.Preset?.Label,
        details = _current.Preset?.Details,
        state = _current.Preset?.State,
        lastDiscordError = _discord.LastError,
        lastDiscordMessage = _discord.LastMessage,
    };

    private static PresenceRequest NormalizeMorningPreset(PresenceRequest request)
    {
        return request;
    }

    private async Task ApplyPresenceAsync(PresenceRequest request)
    {
        if (request.Preset is null) return;

        var activity = new DiscordActivity
        {
            Type = 0,
            Name = FixedPlaying,
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

    private async Task ForceDiscordRefreshAsync()
    {
        await _discord.ClearActivityAsync();
        await Task.Delay(350);
        _discord.Reconnect();
        await Task.Delay(350);
        await ApplyPresenceAsync(_current);
    }


    private async Task CheckRemotePresenceAsync()
    {
        try
        {
            var json = await FetchRemotePresenceJsonAsync(_cts.Token);
            var shared = JsonSerializer.Deserialize<SharedPresetState>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var remote = shared?.Remote;
            if (remote is null || string.IsNullOrWhiteSpace(remote.Active) || string.IsNullOrWhiteSpace(remote.UpdatedAt)) return;
            if (string.Equals(remote.UpdatedAt, _lastRemoteUpdate, StringComparison.Ordinal)) return;
            if (DateTimeOffset.TryParse(remote.UpdatedAt, out var remoteUpdatedAt) && remoteUpdatedAt <= _lastLocalActivation)
            {
                _lastRemoteUpdate = remote.UpdatedAt;
                return;
            }

            PresetTextEdit? edit = null;
            shared?.TextEdits?.TryGetValue(remote.Active, out edit);
            var custom = shared?.CustomPresets?.FirstOrDefault(p => string.Equals(p.Id, remote.Active, StringComparison.OrdinalIgnoreCase));
            var request = PresenceRequest.FromPresetId(remote.Active, edit, remote.StartedAt, custom) with
            {
                AwakeOffTime = string.IsNullOrWhiteSpace(shared?.AwakeOffTime) ? "22:00" : shared.AwakeOffTime,
            };
            await ActivateAsync(request);
            _lastRemoteUpdate = remote.UpdatedAt;
        }
        catch
        {
            // Offline, rate-limited, or GitHub temporarily unavailable. Try again on the next tick.
        }
    }

    private async Task<string> FetchRemotePresenceJsonAsync(CancellationToken cancellationToken)
    {
        var url = $"{RemotePresenceUrl}&t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LoFiRoomCompanion/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var file = JsonSerializer.Deserialize<GitHubContentFile>(payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (string.IsNullOrWhiteSpace(file?.Content)) return "{}";
        var clean = file.Content.Replace("\n", "").Replace("\r", "");
        return Encoding.UTF8.GetString(Convert.FromBase64String(clean));
    }

    private void CheckIdleState()
    {
        if (_current.Preset is null) return;

        var currentPart = PresenceRequest.CurrentTimeOfDay();
        if (!string.Equals(_current.TimeOfDay, currentPart, StringComparison.OrdinalIgnoreCase))
        {
            _current = _current with { TimeOfDay = currentPart };
        }

        TurnOffAwakeIfNeeded(currentPart);
        RefreshGamingPresence(currentPart);

        if (_current.Preset is null || _current.Preset.Id is "away" or "busy") return;

        var idle = IdleSeconds();
        var awayAfter = 5 * 60;

        if (!_awayApplied && idle >= awayAfter)
        {
            _awayApplied = true;
            _beforeAway = _current;
            var away = _current with
            {
                Preset = new PresetPayload
                {
                    Id = "away",
                    Label = "Away",
                    Playing = FixedPlaying,
                    Details = "Stepped away for a bit",
                    State = "Back soon",
                    ArtworkKey = "away",
                },
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            _ = ApplyPresenceAsync(away);
        }
        else if (_awayApplied && idle < 3)
        {
            _awayApplied = false;
            var restored = (_beforeAway ?? PresenceRequest.Chilling()) with
            {
                StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                TimeOfDay = currentPart,
            };
            _beforeAway = null;
            _current = restored;
            _ = ApplyPresenceAsync(restored);
        }
    }


    private void TurnOffAwakeIfNeeded(string currentPart)
    {
        return;
    }

    private static bool IsAtOrAfter(string? time)
    {
        if (!TimeSpan.TryParse(time, out var offTime))
        {
            offTime = new TimeSpan(22, 0, 0);
        }

        return DateTime.Now.TimeOfDay >= offTime;
    }

    private static bool IsMorningBeforeAwakeOff(string? time)
    {
        if (!TimeSpan.TryParse(time, out var offTime))
        {
            offTime = new TimeSpan(22, 0, 0);
        }

        var now = DateTime.Now.TimeOfDay;
        return now >= new TimeSpan(6, 0, 0) && now < offTime;
    }

    private static bool StartedToday(long startedAt)
    {
        if (startedAt <= 0) return false;
        return DateTimeOffset.FromUnixTimeSeconds(startedAt).LocalDateTime.Date == DateTime.Today;
    }

    private void RefreshGamingPresence(string currentPart)
    {
        if (_current.Preset?.Id != "gaming") return;

        var game = DetectCurrentGame();
        var nextState = string.IsNullOrWhiteSpace(game) ? "Choosing a game" : $"Playing {game}";
        if (string.Equals(nextState, _lastGamingState, StringComparison.Ordinal)) return;

        _lastGamingState = nextState;
        _current = _current with
        {
            TimeOfDay = currentPart,
            Preset = _current.Preset with
            {
                Details = "Gaming mode",
                State = nextState,
                ArtworkKey = "gaming",
            },
        };
        _ = ApplyPresenceAsync(_current);
    }

    private static string? DetectCurrentGame()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;
        GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processName = process.ProcessName;
            var title = GetWindowTitle(handle);
            return FriendlyGameName(processName, title);
        }
        catch
        {
            return null;
        }
    }

    private static string? FriendlyGameName(string processName, string title)
    {
        var key = processName.ToLowerInvariant();
        var text = $"{processName} {title}".ToLowerInvariant();

        if (text.Contains("palworld")) return "Palworld";
        if (text.Contains("minecraft") || (key == "javaw" && text.Contains("1."))) return "Minecraft";
        if (text.Contains("fivem")) return "FiveM";
        if (text.Contains("roblox")) return "Roblox";
        if (text.Contains("fortnite")) return "Fortnite";
        if (text.Contains("valorant")) return "Valorant";
        if (text.Contains("league of legends") || key.Contains("leagueclient")) return "League of Legends";
        if (text.Contains("overwatch")) return "Overwatch";
        if (key.Contains("r5apex") || text.Contains("apex legends")) return "Apex Legends";
        if (text.Contains("deadbydaylight")) return "Dead by Daylight";
        if (text.Contains("phasmophobia")) return "Phasmophobia";
        if (text.Contains("among us")) return "Among Us";
        if (text.Contains("terraria")) return "Terraria";
        if (text.Contains("stardew")) return "Stardew Valley";
        if (text.Contains("rocketleague") || text.Contains("rocket league")) return "Rocket League";
        if (text.Contains("sims 4") || text.Contains("thesims4")) return "The Sims 4";
        if (text.Contains("gta5") || text.Contains("grand theft auto")) return "GTA V";

        return null;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : "";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    private static string BuildArtworkKey(string? baseKey, string? timeOfDay)
    {
        var key = string.IsNullOrWhiteSpace(baseKey) ? "chilling" : baseKey;
        var simpleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "busy",
            "away",
            "ems",
            "gaming",
            "chilling",
            "training",
            "busy",
            "away",
            "ems",
            "gaming",
            "chilling",
            "training",
        };

        if (simpleKeys.Contains(key)) return key.ToLowerInvariant();

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
        _remoteTimer.Stop();
        _listener.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _http.Dispose();
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
    public string? LastError { get; private set; }
    public string? LastMessage { get; private set; }

    public async Task SetActivityAsync(DiscordActivity activity)
    {
        await EnsureConnectedAsync();
        LastError = null;
        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity },
            nonce = Guid.NewGuid().ToString("N"),
        };
        await SendAsync(1, payload);
    }

    public async Task ClearActivityAsync()
    {
        await EnsureConnectedAsync();
        LastError = null;
        var payload = new
        {
            cmd = "SET_ACTIVITY",
            args = new { pid = Environment.ProcessId, activity = (object?)null },
            nonce = Guid.NewGuid().ToString("N"),
        };
        await SendAsync(1, payload);
    }

    public void Reconnect()
    {
        _pipe?.Dispose();
        _pipe = null;
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
                RememberReply(buffer);
            }
            catch
            {
                break;
            }
        }
    }

    private void RememberReply(byte[] buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer);
            var root = doc.RootElement;
            var evt = root.TryGetProperty("evt", out var evtElement) ? evtElement.GetString() : null;
            if (string.Equals(evt, "ERROR", StringComparison.OrdinalIgnoreCase))
            {
                LastError = root.TryGetProperty("data", out var data) ? data.ToString() : root.ToString();
                LastMessage = "ERROR";
                return;
            }

            LastMessage = string.IsNullOrWhiteSpace(evt) ? root.GetProperty("cmd").GetString() : evt;
        }
        catch
        {
            LastMessage = "Unread Discord reply";
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
    public string? AwakeOffTime { get; init; } = "22:00";
    public bool ElapsedEnabled { get; init; } = true;
    public bool LaunchWithWindows { get; init; } = true;
    public bool StartMinimized { get; init; } = true;
    public string? TimeOfDay { get; init; } = "morning";

    public static PresenceRequest Chilling() => new()
    {
        Preset = new PresetPayload
        {
            Id = "chilling",
            Label = "Chilling",
            Playing = "Kizzy's Corner",
            Details = "Chilling for the night",
            State = "Cozy mode",
            ArtworkKey = "chilling",
        },
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        TimeOfDay = CurrentTimeOfDay(),
    };

    public static PresenceRequest Awake() => Chilling();

    public static PresenceRequest Busy() => new()
    {
        Preset = new PresetPayload
        {
            Id = "busy",
            Label = "Busy",
            Playing = "Kizzy's Corner",
            Details = "Focus mode activated",
            State = "Headphones on",
            ArtworkKey = "busy",
        },
        StartedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        TimeOfDay = CurrentTimeOfDay(),
    };


    public static PresenceRequest FromPresetId(string id, PresetTextEdit? edit = null, long startedAt = 0, RemotePresetDefinition? custom = null)
    {
        var request = custom is not null ? Build(custom.Id, custom.Name, custom.Details, custom.State, custom.ArtworkKey) : id.ToLowerInvariant() switch
        {
            "busy" => Busy(),
            "chilling" => Build("chilling", "Chilling", "Chilling for the night", "Cozy mode", "chilling"),
            "away" => Build("away", "Away", "Stepped away for a bit", "Back soon", "away"),
            "on-duty" => Build("on-duty", "On Duty", "Responding to calls", "In the city", "ems"),
            "training" => Build("training", "Training / Interviews", "Training and interviews", "EMS prep", "training"),
            "interviews" => Build("training", "Training / Interviews", "Training and interviews", "EMS prep", "training"),
            "gaming" => Build("gaming", "Gaming", "Gaming mode", "Choosing a game", "gaming"),
            _ => Chilling(),
        };

        if (request.Preset is not null && edit is not null)
        {
            request = request with
            {
                Preset = request.Preset with
                {
                    Details = string.IsNullOrWhiteSpace(edit.Details) ? request.Preset.Details : edit.Details,
                    State = string.IsNullOrWhiteSpace(edit.State) ? request.Preset.State : edit.State,
                },
            };
        }

        return request with
        {
            StartedAt = startedAt > 0 ? startedAt : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            TimeOfDay = CurrentTimeOfDay(),
        };
    }

    private static PresenceRequest Build(string id, string label, string details, string state, string artworkKey) => new()
    {
        Preset = new PresetPayload
        {
            Id = id,
            Label = label,
            Playing = "Kizzy's Corner",
            Details = details,
            State = state,
            ArtworkKey = artworkKey,
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

internal sealed record SharedPresetState
{
    public Dictionary<string, PresetTextEdit>? TextEdits { get; init; }
    public RemotePresetDefinition[]? CustomPresets { get; init; }
    public string? AwakeOffTime { get; init; }
    public RemotePresetState? Remote { get; init; }
}

internal sealed record GitHubContentFile
{
    public string? Content { get; init; }
}

internal sealed record RemotePresetDefinition
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Custom";
    public string Details { get; init; } = "Doing something cozy";
    public string State { get; init; } = "Kizzy's Corner";
    public string ArtworkKey { get; init; } = "busy";
}

internal sealed record RemotePresetState
{
    public string Active { get; init; } = "chilling";
    public long StartedAt { get; init; }
    public string UpdatedAt { get; init; } = "";
}

internal sealed record PresetTextEdit
{
    public string? Details { get; init; }
    public string? State { get; init; }
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
    [JsonPropertyName("type")]
    public int Type { get; init; }

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


