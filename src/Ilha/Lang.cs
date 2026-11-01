using System;
using System.Globalization;

namespace Ilha;

/// <summary>
/// Interface language. Follows the Windows display language by default ("auto": Portuguese when Windows is in
/// Portuguese, English otherwise) and can be forced to "pt" or "en" in config ("language"). Every user-facing text is
/// written as <c>Lang.T("português", "English")</c>; logs and code stay as they are.
/// </summary>
internal static class Lang
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    /// <summary>"auto", "pt" or "en" (what the config asked for).</summary>
    public static string Choice { get; private set; } = "auto";
    public static bool English { get; private set; }
    /// <summary>Culture for dates and numbers shown to the user.</summary>
    public static CultureInfo Culture => English ? EnglishCulture : Portuguese;

    /// <summary>Applies the config choice. Returns true when the resulting language changed.</summary>
    public static bool Set(string? choice)
    {
        string c = (choice ?? "auto").Trim().ToLowerInvariant();
        if (c is not ("pt" or "en")) c = "auto";
        bool english = c switch
        {
            "pt" => false,
            "en" => true,
            _ => !string.Equals(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, "pt", StringComparison.OrdinalIgnoreCase)
                 && !string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "pt", StringComparison.OrdinalIgnoreCase),
        };
        Choice = c;
        if (english == English) return false;
        English = english;
        return true;
    }

    public static string T(string pt, string en) => English ? en : pt;
}

/// <summary>Label of a config field in the settings window, in both languages (wins over [DisplayName]).</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class LabelAttribute : Attribute
{
    public string Pt { get; }
    public string En { get; }
    public LabelAttribute(string pt, string en) { Pt = pt; En = en; }
    public string Text => Lang.T(Pt, En);
}

/// <summary>Help line under a config field in the settings window, in both languages (wins over [Description]).</summary>
[AttributeUsage(AttributeTargets.Property)]
internal sealed class HelpAttribute : Attribute
{
    public string Pt { get; }
    public string En { get; }
    public HelpAttribute(string pt, string en) { Pt = pt; En = en; }
    public string Text => Lang.T(Pt, En);
}
