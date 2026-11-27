namespace SdKontrol.API.Services;

/// <summary>
/// Tüm DB, Python, SAP ve port ayarlarını doğrudan SD/.env (veya SD/app/.env) dosyasından okur.
/// app/appsettings.json içinde veritabanı şifresi veya sabit bağlantı cümlesi tutulmaz.
/// </summary>
public sealed class EnvConfig
{
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);
    public string SdRootDir { get; }
    public string AppDir { get; }
    public string EnvFilePath { get; }

    public EnvConfig(IWebHostEnvironment hostEnv)
    {
        AppDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        SdRootDir = ResolveSdRoot(hostEnv.ContentRootPath, AppDir);

        // Önce SD/.env okunur; eğer kullanıcı app/.env içine de koyduysa o da desteklenir
        var rootEnv = Path.Combine(SdRootDir, ".env");
        var appEnv = Path.Combine(AppDir, ".env");
        if (File.Exists(rootEnv))
        {
            EnvFilePath = rootEnv;
            LoadDotEnv(rootEnv);
        }
        else if (File.Exists(appEnv))
        {
            EnvFilePath = appEnv;
            LoadDotEnv(appEnv);
        }
        else
        {
            EnvFilePath = rootEnv;
        }
    }

    private static string ResolveSdRoot(string contentRoot, string baseDir)
    {
        // SD/app/SdKontrol.API.exe çalışırken bir üst klasör SD/ klasörüdür (.env ve start.bat oradadır)
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..")),
            contentRoot,
            Path.GetFullPath(Path.Combine(contentRoot, "..")),
            baseDir
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && (
                File.Exists(Path.Combine(c, ".env")) ||
                File.Exists(Path.Combine(c, "start.bat")) ||
                File.Exists(Path.Combine(c, ".env.example"))))
            {
                return c;
            }
        }
        return Path.GetFullPath(Path.Combine(baseDir, ".."));
    }

    private void LoadDotEnv(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim().Trim('"', '\'');
            _env[key] = val;
        }
    }

    public string Get(string key, string fallback = "")
    {
        // .env dosyası her zaman birincil kaynaktır; yoksa işletim sistemi ortam değişkenine ve varsayılana düşer
        if (_env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        var osVal = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(osVal)) return osVal.Trim();
        return fallback;
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var n) ? n : fallback;

    public string DbHost => Get("DB_HOST", "localhost");
    public int DbPort => GetInt("DB_PORT", 5432);
    public string DbName => Get("DB_NAME", "ud_kokpit");
    public string DbUser => Get("DB_USER", "postgres");
    public string DbPassword => Get("DB_PASSWORD", "");
    public string DbSchema => Get("DB_SCHEMA", "sd");

    public string ConnectionString =>
        $"Host={DbHost};Port={DbPort};Database={DbName};Username={DbUser};Password={DbPassword};SearchPath={DbSchema}";

    public string PythonExe => Get("PYTHON_EXE", "python");
    public string SapLogonPath => Get("SAP_LOGON_PATH", @"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\saplogon.exe");
    public string SapConnection => Get("SAP_CONNECTION", "KKP CANLI");
    public string SapClient => Get("SAP_CLIENT", "500");
    public string SapLanguage => Get("SAP_LANGUAGE", "TR");
    public string SapDefaultSpart => Get("SAP_DEFAULT_SPART", "10");

    public int BackendPort => GetInt("BACKEND_PORT", 5100);
    public int FrontendPort => GetInt("FRONTEND_PORT", 3100);
    public string VeriKlasoru
    {
        get
        {
            var custom = Get("SD_DATA_DIR", "");
            return string.IsNullOrWhiteSpace(custom) ? SdRootDir : custom;
        }
    }

    public string AiProvider => Get("AI_PROVIDER", "");
    public string AiApiUrl => Get("AI_API_URL", "");
    public string AiApiKey => Get("AI_API_KEY", "");
    public string AiModel => Get("AI_MODEL", "");

    // --- MCP Sunucusu (LLM Suite Flow 'MCP Tools' bileşeninin bağlanacağı bağımsız port) ---
    public int McpPort => GetInt("MCP_PORT", 5101);
    public string McpApiKey => Get("MCP_API_KEY", "");
    public string McpPublicUrl => Get("MCP_PUBLIC_URL", "");
    public bool McpSqlEnabled => !Get("MCP_SQL_ENABLED", "true").Equals("false", StringComparison.OrdinalIgnoreCase);

    // --- ASELSAN LLM Suite Flow (Langflow) ---
    public string LlmSuiteBaseUrl => Get("LLMSUITE_BASE_URL", "https://llmsuiteflow.aiplatform.aselsan.com.tr");
    public string LlmSuiteFlowId => Get("LLMSUITE_FLOW_ID", "");
    public string LlmSuiteApiKey => Get("LLMSUITE_API_KEY", "");
    public int LlmSuiteTimeoutSec => GetInt("LLMSUITE_TIMEOUT_SEC", 300);
    public string LlmSuiteChatInputId => Get("LLMSUITE_CHAT_INPUT_ID", "");
    public bool LlmSuiteSslDogrula => !Get("LLMSUITE_SSL_DOGRULA", "true").Equals("false", StringComparison.OrdinalIgnoreCase);

    private static readonly object WriteLock = new();

    /// <summary>Ayarı hem bellekte hem SD/.env dosyasında günceller (anahtar yoksa dosya sonuna ekler).</summary>
    public void Set(string key, string? value)
    {
        var v = (value ?? "").Replace("\r", "").Replace("\n", "").Trim();
        lock (WriteLock)
        {
            _env[key] = v;
            var lines = File.Exists(EnvFilePath) ? File.ReadAllLines(EnvFilePath).ToList() : new List<string>();
            var found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith('#')) continue;
                var idx = t.IndexOf('=');
                if (idx <= 0) continue;
                if (string.Equals(t[..idx].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={v}";
                    found = true;
                    break;
                }
            }
            if (!found) lines.Add($"{key}={v}");
            File.WriteAllLines(EnvFilePath, lines);
        }
    }
}
