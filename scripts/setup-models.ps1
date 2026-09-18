$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../OCRPrototype'
$data = Join-Path $project 'tessdata'
$models = Join-Path $project 'models'
New-Item -ItemType Directory -Force $data, $models | Out-Null
foreach ($language in @('ara', 'eng')) {
    Invoke-WebRequest "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/$language.traineddata" -OutFile (Join-Path $data "$language.traineddata")
}
Invoke-WebRequest 'https://raw.githubusercontent.com/opencv/opencv/4.11.0/data/haarcascades/haarcascade_frontalface_default.xml' -OutFile (Join-Path $models 'haarcascade_frontalface_default.xml')
Write-Host 'Models installed. Start the project using a Windows x64 .NET 9 SDK.'
