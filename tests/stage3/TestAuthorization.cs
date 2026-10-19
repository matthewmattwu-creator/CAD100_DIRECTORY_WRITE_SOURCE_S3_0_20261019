// TEST DOUBLE ONLY. Included only in the isolated QA library compiled by run_tests.ps1.
// Production DirectoryChecking.dll references the unmodified CadProcessingCore license guard.
using System;
namespace CadFrameReplacer {
 public static class CoreLicenseGuard {
  public static bool Deny;
  public static int Calls;
  public static void EnsureAuthorized(string operation) {
   Calls++;
   if(operation!="directory-apply")throw new Exception("Unexpected authorization operation");
   if(Deny)throw new UnauthorizedAccessException("TEST denial");
  }
 }
}
