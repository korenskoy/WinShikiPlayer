param([string]$Arguments = '', [ValidateRange(0,60)][int]$WaitSeconds = 0, [switch]$KeepAlive)
$ErrorActionPreference = 'Stop'
$package = Get-AppxPackage -Name WinShikiPlayer
if (-not $package) { throw 'MSIX WinShikiPlayer не установлен.' }
if (-not ('WinShikiActivation' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWinShikiActivationManager {
 [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,[MarshalAs(UnmanagedType.LPWStr)] string args,uint options,out uint pid);
 [PreserveSig] int ActivateForFile(IntPtr items,[MarshalAs(UnmanagedType.LPWStr)] string verb,out uint pid);
 [PreserveSig] int ActivateForProtocol(IntPtr items,out uint pid);
}
[ComImport,Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")] public class WinShikiActivationManager {}
public static class WinShikiActivation {
 public static uint Launch(string id,string args) {
  uint pid;
  var manager=(IWinShikiActivationManager)new WinShikiActivationManager();
  int status=manager.ActivateApplication(id,args,2,out pid);
  Marshal.ThrowExceptionForHR(status); return pid;
 }
}
'@
}
$applicationId = $package.PackageFamilyName + '!App'
$applicationProcessId = [WinShikiActivation]::Launch($applicationId, $Arguments)
Write-Host "WinShikiPlayer PID: $applicationProcessId"
if ($WaitSeconds -gt 0 -or $KeepAlive) {
    $applicationProcess = Get-Process -Id $applicationProcessId -ErrorAction SilentlyContinue
    if ($applicationProcess) {
        if ($KeepAlive) { $applicationProcess.WaitForExit() } else { $applicationProcess.WaitForExit($WaitSeconds * 1000) | Out-Null }
    }
}
