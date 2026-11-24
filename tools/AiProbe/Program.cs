using System.Text.Json;
using AggPanSearch.AI;
using MySqlConnector;

string configPath = args.Length > 0 ? args[0] : "AggPanSearch.Web/AggPanSearch.Admin.Web/appsettings.json";
if (!File.Exists(configPath))
{
    Console.WriteLine($"找不到配置文件：{Path.GetFullPath(configPath)}");
    return 2;
}

string connStr = FindConnectionString(File.ReadAllText(configPath));
if (string.IsNullOrWhiteSpace(connStr))
{
    Console.WriteLine("配置文件中没有找到数据库连接串");
    return 2;
}

Dictionary<string, string> conf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
await using (MySqlConnection conn = new MySqlConnection(connStr))
{
    await conn.OpenAsync();
    await using MySqlCommand cmd = new MySqlCommand(
        "SELECT ConfKey, ConfValue FROM pan_conf WHERE ConfKey IN ('ai_provider','ai_base_url','ai_model','ai_deepseek_apikey')", conn);
    await using MySqlDataReader reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        conf[reader.GetString(0)] = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
    }
}

string provider = conf.TryGetValue("ai_provider", out string p) ? p.Trim() : string.Empty;
string baseUrl = conf.TryGetValue("ai_base_url", out string b) ? b.Trim() : string.Empty;
string model = conf.TryGetValue("ai_model", out string m) ? m.Trim() : string.Empty;
string key = conf.TryGetValue("ai_deepseek_apikey", out string k) ? (k ?? string.Empty) : string.Empty;

Console.WriteLine("[1] 配置读取");
Console.WriteLine($"    ai_provider  = {(provider.Length == 0 ? "(空，回退 deepseek)" : provider)}");
Console.WriteLine($"    ai_base_url  = {(baseUrl.Length == 0 ? "(空，回退厂商默认)" : baseUrl)}");
Console.WriteLine($"    ai_model     = {(model.Length == 0 ? "(空，回退厂商默认)" : model)}");
string preview = key.Length >= 10 ? key.Substring(0, 6) + "***" + key.Substring(key.Length - 4) : "(过短)";
Console.WriteLine($"    ai_deepseek_apikey = 长度 {key.Length}，{preview}，含空白 {key.Any(char.IsWhiteSpace)}");

if (key.Trim().Length == 0)
{
    Console.WriteLine("    ✗ 未读到 Key，请先在后台「网盘管理 - AI设置」保存");
    return 3;
}

IAiChatClient client;
try
{
    client = AiProviderRegistry.Create(new AiProviderOptions
    {
        ProviderKey = provider,
        BaseUrl = baseUrl,
        ApiKey = key.Trim(),
        Model = model,
        TimeoutSeconds = 30,
        MaxTokens = 600
    });
    Console.WriteLine($"    → 客户端就绪：provider={client.ProviderKey}，baseUrl={baseUrl}，model={client.Model}");
}
catch (AiException ex)
{
    Console.WriteLine($"    ✗ [{ex.Kind}] {ex.Message}");
    return 4;
}

Console.WriteLine();
Console.WriteLine("[2] 模型探测 GET /models");
try
{
    if (client is IAiModelCatalog catalog)
    {
        IReadOnlyList<string> models = await catalog.ListModelsAsync();
        Console.WriteLine($"    可用模型 {models.Count} 个：{string.Join(", ", models)}");
    }
    else
    {
        Console.WriteLine("    当前客户端不支持模型列表探测");
    }
}
catch (AiException ex)
{
    Console.WriteLine($"    ✗ [{ex.Kind}] {ex.Message}（HTTP {ex.StatusCode}）");
}

Console.WriteLine();
Console.WriteLine("[3] 真实对话 POST /chat/completions");
try
{
    AiChatRequest request = AiChatRequest.FromPrompt(
        "你是网盘资源介绍文案助手。只输出一段中文介绍，不要标题、不要引号、不要任何解释。",
        "资源标题：三体 全集 4K HDR；分类：影视；分享信息：8 个文件，共 42.6 GB；文件清单：三体.S01E01.mkv、三体.S01E02.mkv、三体.S01E03.mkv、三体.S01E04.mkv、三体.S01E05.mkv、三体.S01E06.mkv、三体.S01E07.mkv、三体.S01E08.mkv");

    AiChatResponse response = await client.ChatAsync(request);
    string cleaned = AiTextCleaner.Clean(response.Content, 200);
    Console.WriteLine($"    ✓ 成功 model={response.Model} {response.Usage} 耗时={response.ElapsedMs}ms");
    Console.WriteLine("    ---- 原始正文 ----");
    Console.WriteLine("    " + response.Content.Replace("\n", "\n    "));
    Console.WriteLine("    ---- 清洗后 ----");
    Console.WriteLine("    " + cleaned.Replace("\n", "\n    "));
    Console.WriteLine($"    清洗后字数：{cleaned.Length}");
}
catch (AiException ex)
{
    Console.WriteLine($"    ✗ [{ex.Kind}] {ex.Message}（HTTP {ex.StatusCode}）");
    return 5;
}

return 0;

static string FindConnectionString(string json)
{
    JsonDocumentOptions options = new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    using JsonDocument document = JsonDocument.Parse(json, options);
    return Walk(document.RootElement);
}

static string Walk(JsonElement element)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string hit = Walk(property.Value);
                if (!string.IsNullOrEmpty(hit))
                {
                    return hit;
                }
            }
            break;
        case JsonValueKind.Array:
            foreach (JsonElement item in element.EnumerateArray())
            {
                string hit = Walk(item);
                if (!string.IsNullOrEmpty(hit))
                {
                    return hit;
                }
            }
            break;
        case JsonValueKind.String:
            string value = element.GetString() ?? string.Empty;
            if (value.Contains("database=", StringComparison.OrdinalIgnoreCase)
                && value.Contains("server", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
            break;
    }
    return string.Empty;
}
