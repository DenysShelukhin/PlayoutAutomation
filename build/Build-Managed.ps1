# EWTN additions 2026-10-29. GPL-2.0; see LICENSE. Builds only, never deploys or changes system policy.
param(
    [Parameter(Mandatory=$true)][string]$VendorNativeAssembly,
    [string]$DotnetRoot='C:\Program Files\dotnet',
    [string]$SdkVersion='8.0.301'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$tools=Join-Path $repo '.buildtools'
$packages=Join-Path $tools 'packages'
$msbuild=Join-Path $tools 'msbuild'
$sdk=Join-Path (Join-Path $DotnetRoot 'sdk') $SdkVersion
$dotnet=Join-Path $DotnetRoot 'dotnet.exe'
$vendor=(Resolve-Path -LiteralPath $VendorNativeAssembly).Path
if(!(Test-Path -LiteralPath ($sdk+'\Sdks\Microsoft.NET.Sdk'))){throw "Install/locate the declared .NET SDK $SdkVersion first"}
New-Item -ItemType Directory -Path $tools,$msbuild -Force|Out-Null
& $dotnet restore (Join-Path $PSScriptRoot 'Tooling.csproj') --packages $packages -v:quiet
if($LASTEXITCODE -ne 0){throw 'Tooling package restore failed'}
$content=Join-Path $packages 'microsoft.build.runtime\17.10.4\contentFiles\any\net472'
# Explicit SDK path is used. Do not install incomplete optional NuGet SDK resolver manifests.
Get-ChildItem -LiteralPath $content|Where-Object Name -ne 'SdkResolvers'|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $msbuild -Recurse -Force}
$assets=Get-Content (Join-Path $PSScriptRoot 'obj\project.assets.json') -Raw|ConvertFrom-Json
foreach($package in $assets.targets.'.NETFramework,Version=v4.8'.PSObject.Properties){
    $libraryPath=$assets.libraries.($package.Name).path
    foreach($runtime in $package.Value.runtime.PSObject.Properties){
        $file=Join-Path (Join-Path $packages $libraryPath) $runtime.Name
        if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination $msbuild -Force}
    }
}
Copy-Item -LiteralPath (Join-Path $sdk 'NuGet.targets') -Destination $msbuild -Force
$compiler=Join-Path $packages 'microsoft.net.compilers.toolset\4.10.0\tasks\net472'
New-Item -ItemType Directory -Path (Join-Path $msbuild 'Roslyn') -Force|Out-Null
Get-ChildItem -LiteralPath $compiler|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $msbuild 'Roslyn') -Recurse -Force}
$references=Join-Path $tools 'references\.NETFramework'
New-Item -ItemType Directory -Path $references -Force|Out-Null
foreach($entry in @(@('net45','v4.5'),@('net48','v4.8'))){
    $source=Join-Path $packages ('microsoft.netframework.referenceassemblies.'+$entry[0]+'\1.0.3\build\.NETFramework\'+$entry[1])
    $destination=Join-Path $references $entry[1]
    New-Item -ItemType Directory -Path $destination -Force|Out-Null
    Get-ChildItem -LiteralPath $source|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $destination -Recurse -Force}
}
$oldSdks=$env:MSBuildSDKsPath
$oldResolver=$env:MSBuildEnableWorkloadResolver
try {
    $env:MSBuildSDKsPath=Join-Path $sdk 'Sdks'
    $env:MSBuildEnableWorkloadResolver='false'
    $properties=@('/p:Configuration=Release','/p:Platform=x64',('/p:SolutionDir='+$repo+'\'),
        ('/p:TargetFrameworkRootPath='+(Join-Path $tools 'references')+'\'),
        ('/p:CustomBeforeMicrosoftCommonTargets='+(Join-Path $PSScriptRoot 'VendorNative.targets')),
        ('/p:EwtnVendorNativeAssembly='+$vendor),('/p:CscToolPath='+$compiler+'\'),'/p:CscToolExe=csc.exe',
        '/p:UseSharedCompilation=false','/p:GenerateSatelliteAssembliesForCore=true',
        '/p:GenerateResourceMSBuildArchitecture=CurrentArchitecture','/p:GenerateResourceMSBuildRuntime=CurrentRuntime')
    foreach($project in 'TVPlay\TVPlay.csproj','TVPlay.config\TVPlay.config.csproj','TAS.Remoting.Proxy\TAS.Remoting.Proxy.csproj','tests\Ingest.Tests\Ingest.Tests.csproj'){
        & $dotnet restore (Join-Path $repo $project) -v:quiet
        if($LASTEXITCODE -ne 0){throw "Restore failed: $project"}
        & (Join-Path $msbuild 'MSBuild.exe') (Join-Path $repo $project) /t:Build @properties /nr:false /m:1 /v:minimal /nologo
        if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
    }
    & (Join-Path $repo 'tests\Ingest.Tests\bin\x64\Release\Ingest.Tests.exe')
    if($LASTEXITCODE -ne 0){throw 'Ingest command tests failed'}
} finally {
    $env:MSBuildSDKsPath=$oldSdks
    $env:MSBuildEnableWorkloadResolver=$oldResolver
}
Write-Host 'Managed build and command tests passed. No application was installed or started.'
