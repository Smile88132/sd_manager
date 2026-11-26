namespace SdKontrol.API.Services;

/// <summary>
/// SD/.env dosyasını okur; ortam değişkenleri varsa öncelik verir, yoksa appsettings.json varsayılanlarına düşer.
/// </summary>
public sealed class EnvConfig
{
    private readonly Dictionary<string, string> _env = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConfiguration _cfg;
    public string SdRootDir { get; }
    public string EnvFilePath { get; }

    public EnvConfig(IConfiguration cfg, IWebHostEnvironment hostEnv)
    {
        _cfg = cfg;
        SdRootDir = Path.GetFullPath(Path.Combine(hostEnv.ContentRootPath, ".."));
        EnvFilePath = Path.Combine(SdRootDir, ".env");
        LoadDotEnv(EnvFilePath);
    }

    private void LoadDotEnv(string path)
    {
        if (!File.Exists(path)) return;
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
        var osVal = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(osVal)) return osVal.Trim();
        if (_env.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return fallback;
    }

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out var n) ? n : fallback;

    public string DbHost => Get("DB_HOST", "localhost");
    public int DbPort => GetInt("DB_PORT", 5432);
    public string DbName => Get("DB_NAME", "ud_kokpit");
    public string DbUser => Get("DB_USER", "postgres");
    public string DbPassword => Get("DB_PASSWORD", "563563");
    public string DbSchema => Get("DB_SCHEMA", "sd");

    public string ConnectionString
    {
        get
        {
            if (_env.ContainsKey("DB_HOST") || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DB_HOST")))
            {
                return $"Host={DbHost};Port={DbPort};Database={DbName};Username={DbUser};Password={DbPassword};SearchPath={DbSchema}";
            }
            return _cfg.GetConnectionString("SdDb")
                ?? $"Host=localhost;Port=5432;Database=ud_kokpit;Username=postgres;Password=563563;SearchPath=sd";
        }
    }

    public string PythonExe => Get("PYTHON_EXE", "python");
    public string SapLogonPath => Get("SAP_LOGON_PATH", @"C:\Program Files (x86)\SAP\FrontEnd\SAPgui\saplogon.exe");
    public string SapConnection => Get("SAP_CONNECTION", "KKP CANLI");
    public string SapClient => Get("SAP_CLIENT", "500");
    public string SapLanguage => Get("SAP_LANGUAGE", "TR");
    public string SapDefaultSpart => Get("SAP_DEFAULT_SPART", "10");

    public int BackendPort => GetInt("BACKEND_PORT", _cfg.GetValue("SdKontrol:Port", 5100));
    public int FrontendPort => GetInt("FRONTEND_PORT", 3100);
    public string VeriKlasoru
    {
        get
        {
            var custom = Get("SD_DATA_DIR", _cfg["SdKontrol:VeriKlasoru"] ?? "");
            return string.IsNullOrWhiteSpace(custom) ? SdRootDir : custom;
        }
    }

    public string AiProvider => Get("AI_PROVIDER", "");
    public string AiApiUrl => Get("AI_API_URL", "");
    public string AiApiKey => Get("AI_API_KEY", "");
    public string AiModel => Get("AI_MODEL", "");
}
