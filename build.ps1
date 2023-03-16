if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") {
    $runtime = "win-arm64"
} else {
    $runtime = "win-x64"
}

remove-item -Recurse -Force $PSScriptRoot\bin
push-location $PSScriptRoot\src
dotnet publish -r $runtime -o ..\bin --no-self-contained
pop-location
