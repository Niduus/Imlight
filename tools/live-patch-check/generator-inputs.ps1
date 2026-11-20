# Puts the game's generator inputs (the IMCODEC_GENERATOR_INPUTS secret, a base64 zip) where Imcodec's build looks for
# them, so a server can be built on GitHub. Same steps as build-check.yml and release.yml.
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllBytes("$env:RUNNER_TEMP/inputs.zip", [Convert]::FromBase64String($env:GENERATOR_INPUTS))
Expand-Archive "$env:RUNNER_TEMP/inputs.zip" "$env:RUNNER_TEMP/inputs" -Force
$codec = "submodule/Imcodec/src"
New-Item -ItemType Directory -Force "$codec/Imcodec.ObjectProperty/GeneratorInput", "$codec/Imcodec.MessageLayer/GeneratorInput" | Out-Null
Copy-Item "$env:RUNNER_TEMP/inputs/ObjectProperty/*" "$codec/Imcodec.ObjectProperty/GeneratorInput/" -Force
Copy-Item "$env:RUNNER_TEMP/inputs/MessageLayer/*" "$codec/Imcodec.MessageLayer/GeneratorInput/" -Force
