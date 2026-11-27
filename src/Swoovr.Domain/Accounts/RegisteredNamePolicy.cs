using System.Globalization;
using System.Text;

namespace Swoovr.Domain;

public sealed record RegisteredNames(string FirstName,string? LastName,bool Mononym);

/// <summary>D300: self-declared names, never proof of verified identity or public-name fallback to surname.</summary>
public static class RegisteredNamePolicy
{
    public const int MaximumLength=100;
    public static string? Part(string? value)
    {
        if(string.IsNullOrWhiteSpace(value)||value.Length>MaximumLength||value.Any(char.IsControl))return null;
        string normal;try{normal=value.Trim().Normalize(NormalizationForm.FormC);}catch(ArgumentException){return null;}
        if(normal.Length==0)return null;
        bool letter=false;
        foreach(var rune in normal.EnumerateRunes())
        {
            var category=Rune.GetUnicodeCategory(rune);
            if(category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter){letter=true;continue;}
            if(category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.DashPunctuation or UnicodeCategory.SpaceSeparator)continue;
            if(rune.Value is 0x20 or 0x27 or 0x2d or 0x2e or 0x2018 or 0x2019 or 0x05f3 or 0x05f4 or 0x200c or 0x200d)continue;
            return null;
        }
        return letter?normal:null;
    }
    public static RegisteredNames? Validate(string? first,string? last,bool mononym)=>Part(first)is{}given
        &&(mononym?string.IsNullOrWhiteSpace(last):Part(last)is not null)?new(given,mononym?null:Part(last),mononym):null;
    public static bool Complete(Account account)=>account.RegisteredNamesConfirmedAt is not null
        &&Validate(account.RegisteredFirstName,account.RegisteredLastName,account.RegisteredMononym)is not null;
    public static string? InitialPublicName(string? current,RegisteredNames names)
    {
        if(!string.IsNullOrWhiteSpace(current))return current;
        var limit=Math.Min(40,names.FirstName.Length);
        if(limit<names.FirstName.Length&&char.IsHighSurrogate(names.FirstName[limit-1]))limit--;
        return names.FirstName[..limit];
    }
    /// <summary>Q192 A (D429): the registered names are the same as the ID's once case, spacing, accents, hyphens and apostrophes are ignored.</summary>
    public static bool Matches(RegisteredNames names,string? first,string? last)=>Compare(names,first,last)==NameComparison.Same;

    /// <summary>
    /// Q192 A (D429, issue 533): the registered names against the names on an approved ID. Case, extra spaces, accents,
    /// hyphens and apostrophes are ignored. When the first given name and the last name agree and only middle names differ
    /// it's MiddleOnly; never accepted automatically, the member is offered the one-time correction. Anything else is
    /// Different. Unknown when the ID gave no given name, so a missing value never makes a mismatch.
    /// </summary>
    public static NameComparison Compare(RegisteredNames names,string? first,string? last)
    {
        var idGiven=Tokens(first);
        if(idGiven.Count==0)return NameComparison.Unknown;
        var idFamily=Tokens(last);
        var given=Tokens(names.FirstName);
        if(names.Mononym)
            return Same([..idGiven,..idFamily],given)?NameComparison.Same:NameComparison.Different;
        var family=Tokens(names.LastName);
        if(Same(given,idGiven)&&Same(family,idFamily))return NameComparison.Same;
        return given.Count>0&&idGiven[0]==given[0]&&Same(family,idFamily)&&family.Count>0?NameComparison.MiddleOnly:NameComparison.Different;
    }

    private static bool Same(IReadOnlyList<string> a,IReadOnlyList<string> b)=>a.SequenceEqual(b)||a.Count>0&&string.Concat(a)==string.Concat(b);

    /// <summary>Lower-case words without accents; hyphens split words, apostrophes are dropped.</summary>
    private static List<string> Tokens(string? value)
    {
        if(string.IsNullOrWhiteSpace(value))return [];
        string decomposed;try{decomposed=value.Normalize(NormalizationForm.FormD);}catch(ArgumentException){return [];}
        var text=new StringBuilder(decomposed.Length);
        foreach(var c in decomposed)
        {
            var category=CharUnicodeInfo.GetUnicodeCategory(c);
            if(category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)continue;
            if(c is '\'' or '\u2018' or '\u2019' or '\u02bc' or '\u05f3')continue;
            text.Append(category==UnicodeCategory.DashPunctuation||char.IsWhiteSpace(c)||c=='.'?' ':char.ToLowerInvariant(c));
        }
        return text.ToString().Normalize(NormalizationForm.FormC).Split(' ',StringSplitOptions.RemoveEmptyEntries).ToList();
    }
}

public enum NameComparison{Same,MiddleOnly,Different,Unknown}

/// <summary>Issue 533 (D429): where the registered name stands after an approved ID check, like <see cref="DobCheck"/>.</summary>
public enum NameCheck{None,CorrectionNeeded,ManualReview}
