namespace ScreenTranslator.Models;

/// <summary>Установленный браузер, найденный в реестре.</summary>
public sealed record BrowserInfo(string Id, string Name, string ExePath)
{
    public const string DefaultId = "default";

    public static BrowserInfo Default { get; } = new(DefaultId, "Браузер по умолчанию", "");

    public bool IsDefault => Id == DefaultId;

    public override string ToString() => Name;
}
