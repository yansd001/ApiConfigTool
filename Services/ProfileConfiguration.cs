using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiConfigTool.Services;

public sealed record ApiProfile(string Id, string Name, string BaseUrl, string ApiKey, string Model, string? ConfigText = null);
public sealed record ApiProfileList(IReadOnlyList<ApiProfile> Profiles, string? ActiveId, CodexConfigurationMode Mode = CodexConfigurationMode.ApiConfiguration);

public interface IProfileConfigService
{
    string ConfigPath { get; }
    string StatePath { get; }
    ApiProfileList LoadProfiles();
    ApiProfile SaveProfile(string? id, string? name, string baseUrl, string apiKey, string model, string? configText = null);
    void ActivateProfile(string id);
    void DeleteProfile(string id);
    ApiProfile ImportCurrent();
    string PreviewConfig(ApiProfile profile);
    ApiProfile ParseConfig(string text, string? id, string? name);
}

internal static class ProfileConfiguration
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ValidateUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new ArgumentException("请填写 Base URL。");
        var url = ModelsApiService.EnsureScheme(input).TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != "https" && parsed.Scheme != "http") || string.IsNullOrEmpty(parsed.Host) ||
            parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 || input.Any(char.IsWhiteSpace))
            throw new ArgumentException("Base URL 必须是有效的 HTTP(S) 地址，不能包含用户信息、查询参数或片段。");
        return url;
    }

    public static void ValidateFields(string key, string model)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("请填写 API Key 和模型名称。");
        if (key.Any(char.IsControl) || model.Any(char.IsControl))
            throw new ArgumentException("API Key 和模型名称不能包含换行或控制字符。");
    }

    public static JsonObject ParseJson(string text, string label)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            RejectDuplicates(doc.RootElement);
            return JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{label} 必须是 JSON 对象。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{label} JSON 语法错误：第 {(ex.LineNumber ?? 0) + 1} 行，第 {(ex.BytePositionInLine ?? 0) + 1} 列。未写入文件。");
        }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("JSON 中存在重复字段，请删除重复项后重试。");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }
}
