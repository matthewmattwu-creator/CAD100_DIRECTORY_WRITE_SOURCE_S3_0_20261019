// TEST DOUBLE ONLY. Does not read or write valid DWG data and must never be shipped as CAD engine.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
[assembly: AssemblyFileVersion("24.0.0.0")]
class FakeCore {
 static int Main(string[] args) {
  string drawing=args[Array.IndexOf(args,"/i")+1], script=args[Array.IndexOf(args,"/s")+1];
  string job=Path.GetDirectoryName(drawing),mode=File.ReadAllLines(drawing)[0];
  bool verify=Path.GetFileName(script).StartsWith("verify");
  if(mode=="external" || mode=="locked" || mode=="invisible"){
   File.WriteAllText(Path.Combine(job,"native_error.txt"),mode=="external"?"External dependencies require manual packaging.":mode=="locked"?"Attribute layer is locked.":"Invisible attribute is not writable in this trial.",new UTF8Encoding(false));return 0;
  }
  if(mode=="exit0")return 0;
  if(mode=="exit1" || (mode=="verifyExit1" && verify))return 1;
  var inventory=new[]{"CAD100_PROTECTED_V1","A\tATTRIB height=3.5","B\tINSERT scale=150.00001811","DONE"};
  if(!verify){
   File.WriteAllLines(Path.Combine(job,"before.txt"),inventory,new UTF8Encoding(false));
   File.WriteAllLines(Path.Combine(job,"after.txt"),inventory,new UTF8Encoding(false));
   File.AppendAllText(drawing,"\nUPDATED-BY-TEST-DOUBLE");
   File.WriteAllText(Path.Combine(job,"written.ok"),"SAVED");return 0;
  }
  if(mode=="geometry")inventory[2]="B\tINSERT scale=150";
  if(mode=="partial")inventory=inventory.Take(2).ToArray();
  File.WriteAllLines(Path.Combine(job,"reopened.txt"),inventory,new UTF8Encoding(false));
  string embedded=mode=="oldVisible"?"Old":"New";
  File.WriteAllLines(Path.Combine(job,"entities.tsv"),new[]{
   "META\tCAD100_DIRECTORY_SCAN_V1\t24.1\t0\tUTF8",
   "ENTITY\tATTRIB\tA\tB\tModel\t0\t图名\tNew\t1\t\\\\W0.8;"+embedded+"\t(1 2 0)\t(1 2 0)\t3.5\t1\t0\t0\t0\t",
   "DONE\t0\t0"},new UTF8Encoding(false));return 0;
 }
}
