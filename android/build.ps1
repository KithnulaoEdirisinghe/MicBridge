# Builds dist\MicBridge.apk straight from the Android SDK tools (no Gradle, no Android Studio).
#
# It needs three things. Each is looked for in this order:
#   a JDK 17 folder (contains bin\javac.exe):
#       MICBRIDGE_JDK, then JAVA_HOME
#   Android SDK build-tools 35 (contains aapt2.exe, zipalign.exe, lib\d8.jar):
#       MICBRIDGE_BUILD_TOOLS, then the newest folder in ANDROID_HOME\build-tools
#   Android SDK platform 35 (contains android.jar):
#       MICBRIDGE_PLATFORM, then the newest folder in ANDROID_HOME\platforms
# (ANDROID_SDK_ROOT works in place of ANDROID_HOME. A folder %LOCALAPPDATA%\Temp\mb-tools holding
#  jdk\, build-tools\ and platform\ is used as a last resort.)
#
# Run:   powershell -ExecutionPolicy Bypass -File android\build.ps1

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent $here
$tools = Join-Path ([IO.Path]::GetFullPath($env:LOCALAPPDATA)) 'Temp\mb-tools'
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }

# Returns the first candidate folder that contains $probe. A candidate ending in \* means "its newest subfolder".
function Find-Tool([string]$what, [string]$probe, [string[]]$candidates) {
    foreach ($c in $candidates) {
        if (-not $c) { continue }
        if ($c.EndsWith('\*')) {
            $parent = $c.Substring(0, $c.Length - 2)
            if (-not (Test-Path -LiteralPath $parent)) { continue }
            foreach ($d in Get-ChildItem -LiteralPath $parent -Directory | Sort-Object Name -Descending) {
                if (Test-Path -LiteralPath (Join-Path $d.FullName $probe)) { return $d.FullName }
            }
        } elseif (Test-Path -LiteralPath (Join-Path $c $probe)) { return $c }
    }
    throw "Could not find $what ($probe). See the top of this script for where it is looked for."
}

$sdkTools = if ($sdk) { Join-Path $sdk 'build-tools\*' } else { $null }
$sdkPlatforms = if ($sdk) { Join-Path $sdk 'platforms\*' } else { $null }
$jdk = Find-Tool 'a JDK' 'bin\javac.exe' @($env:MICBRIDGE_JDK, $env:JAVA_HOME, "$tools\jdk\*")
$bt = Find-Tool 'Android build-tools' 'aapt2.exe' @($env:MICBRIDGE_BUILD_TOOLS, $sdkTools, "$tools\build-tools\*")
$platform = Find-Tool 'an Android platform' 'android.jar' @($env:MICBRIDGE_PLATFORM, $sdkPlatforms, "$tools\platform\*")
$java = Join-Path $jdk 'bin\java.exe'
$androidJar = Join-Path $platform 'android.jar'
$lambdaStubs = Join-Path $bt 'core-lambda-stubs.jar'    # lets javac compile lambdas against android.jar

$minSdk = 24            # Android 7.0
$targetSdk = 34         # Android 14 behaviour
$versionCode = 2
$versionName = '1.1'

function Run([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$build = Join-Path $here 'build'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
New-Item -ItemType Directory -Force $build, "$build\gen", "$build\classes", "$build\dex" | Out-Null
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# 1. Resources -> binary form, plus the generated R.java
Run 'aapt2 compile' { & "$bt\aapt2.exe" compile --dir "$here\res" -o "$build\res.zip" }
Run 'aapt2 link' {
    & "$bt\aapt2.exe" link -o "$build\base.apk" -I $androidJar --manifest "$here\AndroidManifest.xml" `
        --java "$build\gen" --min-sdk-version $minSdk --target-sdk-version $targetSdk `
        --version-code $versionCode --version-name $versionName "$build\res.zip"
}

# 2. Java -> class files -> one small classes.dex
$sources = @(Get-ChildItem "$here\src", "$build\gen" -Recurse -Filter *.java | ForEach-Object { '"' + $_.FullName.Replace('\', '/') + '"' })
[IO.File]::WriteAllLines("$build\sources.txt", $sources)
Run 'javac' {
    & "$jdk\bin\javac.exe" -source 8 -target 8 -encoding UTF-8 -bootclasspath "$androidJar;$lambdaStubs" -Xlint:all -Xlint:-options `
        -d "$build\classes" "@$build\sources.txt"
}
Run 'jar' { & "$jdk\bin\jar.exe" --create --file "$build\classes.jar" -C "$build\classes" . }
Run 'd8' {
    & $java -cp "$bt\lib\d8.jar" com.android.tools.r8.D8 --release --min-api $minSdk --lib $androidJar `
        --output "$build\dex" "$build\classes.jar"
}

# 3. Put the code into the package, align it, sign it
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open("$build\base.apk", 'Update')
try {
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, "$build\dex\classes.dex", 'classes.dex', 'Optimal')
} finally { $zip.Dispose() }
Run 'zipalign' { & "$bt\zipalign.exe" -f -p 4 "$build\base.apk" "$build\aligned.apk" }

# The signing key is created once and reused, so later builds install over the app as updates.
$signing = Join-Path $here 'signing'
$keystore = Join-Path $signing 'micbridge.jks'
$secret = Join-Path $signing 'password.txt'
if (-not (Test-Path $keystore)) {
    New-Item -ItemType Directory -Force $signing | Out-Null
    $bytes = New-Object byte[] 24
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    [IO.File]::WriteAllText($secret, [Convert]::ToBase64String($bytes).Replace('+', 'a').Replace('/', 'b').Replace('=', 'c'))
    Run 'keytool' {
        & "$jdk\bin\keytool.exe" -genkeypair -keystore $keystore -storetype PKCS12 -alias micbridge -keyalg RSA -keysize 2048 `
            -validity 10000 -dname 'CN=MicBridge' "-storepass:file" $secret "-keypass:file" $secret
    }
}
$apk = Join-Path $dist 'MicBridge.apk'
Run 'apksigner sign' {
    & $java -jar "$bt\lib\apksigner.jar" sign --ks $keystore --ks-key-alias micbridge --ks-pass "file:$secret" `
        --min-sdk-version $minSdk --v4-signing-enabled false --out $apk "$build\aligned.apk"
}
Run 'apksigner verify' { & $java -jar "$bt\lib\apksigner.jar" verify --min-sdk-version $minSdk $apk }
Run 'zipalign check' { & "$bt\zipalign.exe" -c 4 $apk }

"Built $apk  ($([math]::Round((Get-Item $apk).Length / 1KB)) KB)"
