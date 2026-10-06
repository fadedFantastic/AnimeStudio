$ErrorActionPreference = 'Stop'

# prepare patcher
dotnet build AnimeStudio.Patcher -c Release -f net10.0
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the apphost patcher.' }
$patcher = "AnimeStudio.Patcher\bin\Release\net10.0\AnimeStudio.Patcher.exe"

foreach ($tfm in 'net9.0-windows', 'net10.0-windows') {
    # config
    $outputDir = ".\dist\$tfm"
    $configuration = 'Release'

    # prepare paths
    $guiOut = "AnimeStudio.GUI/bin/$configuration/$tfm"
    $cliOut = "AnimeStudio.CLI/bin/$configuration/$tfm"

    # Keep the build outputs runnable with their DLLs in the same directory.
    dotnet build AnimeStudio.CLI -c $configuration -f $tfm
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the CLI for $tfm." }
    dotnet build AnimeStudio.GUI -c $configuration -f $tfm
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the GUI for $tfm." }

    # prepare output dir
    if (Test-Path $outputDir) { Remove-Item $outputDir -Recurse -Force }
    New-Item -ItemType Directory $outputDir
    New-Item -ItemType Directory "$outputDir/bin"

    # copy to output
    Copy-Item "$cliOut/*" "$outputDir/bin" -Recurse
    Copy-Item "$guiOut/*" "$outputDir/bin" -Recurse -Force

    # move files out
    foreach ($exe in 'AnimeStudio.GUI.exe', 'AnimeStudio.CLI.exe') {
        Move-Item "$outputDir/bin/$exe" $outputDir
        # Only the distribution copy loads its DLL from the bin subdirectory.
        & $patcher "$outputDir/$exe" -d bin
        if ($LASTEXITCODE -ne 0) { throw "Failed to patch $exe for $tfm." }
    }
    Move-Item "$outputDir/bin/LICENSE" $outputDir
}
