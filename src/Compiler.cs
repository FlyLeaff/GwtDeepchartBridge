using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Gwt;

// Builds GwtLive.dll in memory: the fixed indicator template + LiveData with the current items baked in as Add(...) calls,
// stamped with the member's own Deepchart developer id (Deepchart loads a local dll only when it carries the user's id).
public static class Compiler
{
    public const string AssemblyName = "GwtLive";

    static string Template()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GwtLive.Template.cs")
            ?? throw new InvalidOperationException("template resource missing");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public static string DataSource(IEnumerable<LiveItem> items, long builtMs, string status)
    {
        var b = new StringBuilder();
        b.AppendLine("using System.Collections.Generic;");
        b.AppendLine("namespace GwtLive;");
        b.AppendLine("static class LiveData {");
        b.AppendLine("public const long BuiltMs = " + builtMs + ";");
        b.AppendLine("public const string Status = " + LiveItem.Literal(status) + ";");
        b.AppendLine("public static void Fill(List<It> l) {");   // statements, not an array initialiser: Deepchart rejects RuntimeHelpers.InitializeArray
        foreach (var it in items)
            b.AppendLine(it.ToCode());
        b.AppendLine("} }");
        return b.ToString();
    }

    // refDir: .NET reference assemblies shipped with the bridge; deepchartDir: Deepchart's install folder (VolSysAPI, VolumetricaCore).
    public static byte[] Build(string dataSource, string devId, string refDir, string deepchartDir)
    {
        var parse = new CSharpParseOptions(LanguageVersion.CSharp12);
        string attrs = "[assembly: System.Reflection.AssemblyMetadata(\"DeepchartDevId\", " + LiveItem.Literal(devId) + ")]";
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(Template(), parse, "GwtLive.cs"),
            CSharpSyntaxTree.ParseText(dataSource, parse, "LiveData.cs"),
            CSharpSyntaxTree.ParseText(attrs, parse, "Attrs.cs"),
        };
        var refs = Directory.GetFiles(refDir, "*.dll").Select(f => (MetadataReference)MetadataReference.CreateFromFile(f)).ToList();
        foreach (var n in new[] { "VolSysAPI.dll", "VolumetricaCore.dll" })
            refs.Add(MetadataReference.CreateFromFile(Path.Combine(deepchartDir, n)));
        var comp = CSharpCompilation.Create(AssemblyName, trees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release, deterministic: true, nullableContextOptions: NullableContextOptions.Disable));
        using var ms = new MemoryStream();
        var res = comp.Emit(ms);
        if (!res.Success)
            throw new InvalidOperationException("compile failed: " + string.Join(" | ",
                res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3).Select(d => d.ToString())));
        return ms.ToArray();
    }

    // Write next to the other indicators without ever leaving a half-written dll for Deepchart's folder watcher.
    public static void Deploy(byte[] dll, string indicatorsDir)
    {
        Directory.CreateDirectory(indicatorsDir);
        string target = Path.Combine(indicatorsDir, AssemblyName + ".dll");
        string tmp = Path.Combine(Path.GetTempPath(), AssemblyName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(tmp, dll);
        File.Move(tmp, target, overwrite: true);
    }
}
