namespace MAAUnified.Application.Services.ThirdParty;

/// <summary>
/// Flat persistence keys for the dev-v2 <c>Gui.ThirdParty</c> settings. The reporting toggles
/// keep their frozen v6.3.7 keys (<c>Penguin.*</c> / <c>Yituliu.*</c>) so existing gui.json files
/// keep working; only the fields introduced with the yituliu OpenAPI need new keys.
/// </summary>
public static class ThirdPartySettingKeys
{
    public const string YituliuOpenApiToken = "ThirdParty.YituliuOpenApiToken";

    public const string OperBoxUseYituliuApi = "ThirdParty.OperBoxUseYituliuApi";

    /// <summary>
    /// dev-v2 <c>Gui.ThirdParty.EnableOperBoxAssist</c>: hand the operator-box data file to
    /// Core through Copilot's <c>operbox_data_path</c> when it exists.
    /// </summary>
    public const string EnableOperBoxAssist = "ThirdParty.EnableOperBoxAssist";
}
