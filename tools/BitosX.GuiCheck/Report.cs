using System.IO;

namespace BitosX.GuiCheck;

/// <summary>What was checked and what came of it.</summary>
internal sealed class Report
{
    private readonly List<(string Section, string Check, string Verdict, string Detail)> _entries = [];
    private string _section = string.Empty;

    public int Failed => _entries.Count(e => e.Verdict == "FAIL");

    public int Passed => _entries.Count(e => e.Verdict == "PASS");

    public int Skipped => _entries.Count(e => e.Verdict == "SKIP");

    public void Section(string name)
    {
        _section = name;
        Console.WriteLine();
        Console.WriteLine(name);
    }

    public bool Check(string check, bool passed, string detail = "")
    {
        Add(check, passed ? "PASS" : "FAIL", detail);
        return passed;
    }

    /// <summary>Something that was not checked. Said, not passed over.</summary>
    public void Skip(string check, string why) => Add(check, "SKIP", why);

    /// <summary>
    /// Runs a part of the checklist. A part that cannot be completed — the application did not do
    /// what was waited for — is a failure of that part, and the run goes on with the next.
    /// </summary>
    public void Run(string section, Action part)
    {
        Section(section);
        try
        {
            part();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException
                                       or System.Windows.Automation.ElementNotAvailableException
                                       or System.Runtime.InteropServices.COMException)
        {
            Add("This part could be carried out to the end", "FAIL", ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Summarize()
    {
        Console.WriteLine();
        Console.WriteLine("----------------------------------------");
        foreach ((string section, string check, string verdict, string detail) in _entries.Where(e => e.Verdict != "PASS"))
        {
            Console.WriteLine($"{verdict}  {section}: {check}" + (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
        }

        Console.WriteLine($"RESULT: {Passed} passed, {Failed} failed, {Skipped} not checked.");
    }

    private void Add(string check, string verdict, string detail)
    {
        _entries.Add((_section, check, verdict, detail));
        Console.WriteLine($"  {verdict}  {check}" + (detail.Length > 0 ? $"  [{detail}]" : string.Empty));
    }
}
