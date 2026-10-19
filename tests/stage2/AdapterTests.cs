using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;
using System.Text;
using Cad100.DirectoryChecking;
public static class AdapterTests
{
    static int tests;
    static void Check(bool b, string label) { if (!b) throw new Exception(label); tests++; Console.WriteLine("PASS " + label); }
    static void Reject(Action f, string label) { bool rejected=false; try { f(); } catch (InvalidDataException) { rejected=true; } Check(rejected,label); }
    public static int Main(string[] args)
    { try { return Run(args); } catch(Exception ex) { Console.WriteLine(ex.GetType().FullName+": "+ex.Message); return 1; } }
    public static int Run(string[] args)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var entries=json.Deserialize<List<Dictionary<string,string>>>(File.ReadAllText(args[0]));
        var files=new List<ScanFileData>();
        foreach(var entry in entries) { bool changed; var entities=DwgDirectoryScanner.Parse(entry["tsv"],out changed); files.Add(new ScanFileData { File=entry["dwg"],Hash="qa-replay",Entities=entities }); }
        var baseline=files.Single(f=>Path.GetFileName(f.File).StartsWith("L-P-CL-001"));
        var rows=DwgDirectoryScanner.ExtractDirectory(baseline);
        var commands=DwgDirectoryScanner.ReaderScriptLines();
        Check(commands.All(line=>line.All(c=>c<128)),"SCR remains ASCII regardless of Chinese install/output paths");
        Check(commands.Any(line=>line.Contains("DWGPREFIX") && line.Contains("reader.lsp")) && commands.Any(line=>line.Contains("DWGPREFIX") && line.Contains("entities.tsv")),"native DWG prefix supplies both Unicode paths; none serialized in SCR");
        Check(!commands.Any(line=>line.Contains("C:/") || line.Contains("C:\\")) && commands.Last()=="_Y","no absolute path injection; readonly-copy quit confirms discard");
        string ascii=string.Join("\n",commands);
        Check(Encoding.GetEncoding(936).GetString(Encoding.UTF8.GetBytes(ascii))==ascii,"UTF8-vs-GBK SCR decoding yields identical commands");
        string chinese=Path.Combine(args[2],"中文路径_空格 R1");Directory.CreateDirectory(chinese);
        string entity=string.Join("\t",new[]{"ENTITY","TEXT","ABC","","Model","0","","材料表","0","","(1 2 0)",""});
        foreach(string enc in new[]{"UTF8","CP936","SYSTEM"})
        {
            string pth=Path.Combine(chinese,enc+".tsv");Encoding codec=enc=="CP936"?Encoding.GetEncoding(936):enc=="SYSTEM"?Encoding.Default:Encoding.UTF8;
            File.WriteAllText(pth,"META\tCAD100_DIRECTORY_SCAN_V1\t24.1\t0\t"+enc+"\n"+entity+"\nDONE\t0\t0\n",codec);
            bool ch;Check(DwgDirectoryScanner.Parse(pth,out ch).Single().Ordinary=="材料表",enc+" entity content round-trips under Chinese and spaced paths");
        }
        File.WriteAllText(Path.Combine(args[2],"directory_debug.json"),json.Serialize(rows)); Console.WriteLine("extracted rows="+rows.Count);
        Check(rows.Count==57,"header-and-grid extraction returns 57 directory rows");
        Check(rows.Select(r=>r.Fields["图号"].OrdinaryRaw).Distinct().Count()==57,"57 distinct numbers including dot suffixes");
        var expected=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[1]));
        var records=(System.Collections.ArrayList)expected["native_directory"];
        foreach(Dictionary<string,object> record in records)
        {
            string no=(string)record["图号"]; var actual=rows.Single(r=>r.Fields["图号"].OrdinaryRaw==no);
            if(actual.Fields["图名"].OrdinaryRaw.Trim(' ') != ((string)record["图名"]).Trim(' ')) throw new Exception("directory title mismatch "+no);
        }
        Check(true,"all 57 titles independently match native evidence");
        var input=DwgDirectoryScanner.BuildInput(files,baseline.File);var result=RuleEngine.Evaluate(input);
        Check(input.Titles.Count==10,"extracts exactly 10 actual title blocks, excluding detail labels");
        Check(result.Titles.Count(r=>r.ContentMatches)==4,"four matching title blocks");
        Check(result.Titles.Count(r=>r.Association=="Conflict")==1,"standard-road number/name conflict preserved");
        Check(result.Titles.SelectMany(r=>r.Issues).Count(i=>i.Code=="REPRESENTATION_MISMATCH")==2,"two embedded/ordinary discrepancies preserved");
        Check(result.Titles.Count(r=>r.Association=="ByNamePendingConfirmation")==1,"one empty-number ecological-stream candidate");
        foreach(var e in baseline.Entities)
        {
            for(int j=0;j<e.Position.Length;j++)e.Position[j]=e.Position[j]*7.3+(j==0?12345:-9876);
            for(int j=0;j<e.Alignment.Length;j++)e.Alignment[j]=e.Alignment[j]*7.3+(j==0?12345:-9876);
            e.Height*=7.3;
        }
        var shifted=DwgDirectoryScanner.ExtractDirectory(baseline);
        Check(shifted.Count==57 && shifted.Select(r=>r.Fields["图号"].OrdinaryRaw).OrderBy(x=>x).SequenceEqual(rows.Select(r=>r.Fields["图号"].OrdinaryRaw).OrderBy(x=>x)),"translated and scaled table still extracts; no fixed sample coordinates");
        var broken=new ScanFileData { File="unsupported.dwg",Entities=new List<CadEntity>() };
        Reject(delegate { DwgDirectoryScanner.ExtractDirectory(broken); },"unsupported directory rejected instead of guessed");
        string partial=Path.Combine(args[2],"partial.tsv");File.WriteAllText(partial,"META\tCAD100_DIRECTORY_SCAN_V1\t24.1\t0\n");
        Reject(delegate {bool changed;DwgDirectoryScanner.Parse(partial,out changed);},"partial native export rejected");
        string state=Path.Combine(args[2],"state.tsv");File.WriteAllText(state,"META\tCAD100_DIRECTORY_SCAN_V1\t24.1\t0\nDONE\t0\t1\n");
        bool stateChanged;DwgDirectoryScanner.Parse(state,out stateChanged);Check(stateChanged,"copy DBMOD change is surfaced, not reset or hidden");
        var head=baseline.Entities.First(e=>e.Type=="TEXT" && e.Ordinary=="序号");
        baseline.Entities.Add(new CadEntity{Type="TEXT",Handle="extra-header",Layout=head.Layout,Ordinary="序号",Embedded="",Position=new[]{head.Position[0],head.Position[1]-head.Height*100},Alignment=new double[0],Height=head.Height});
        Reject(delegate {DwgDirectoryScanner.ExtractDirectory(baseline);},"separate directory tables are rejected instead of silently merged");
        File.WriteAllText(Path.Combine(args[2],"adapter_results.json"),json.Serialize(new { passed=tests,failed=0,scope="Native TXT replay and synthetic transformations; live Core Console not executed by these tests." }));
        File.WriteAllText(Path.Combine(args[2],"adapter_input.json"),json.Serialize(input));
        Console.WriteLine("Adapter tests passed: "+tests); return 0;
    }
}
