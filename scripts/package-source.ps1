param([string]$OutputDirectory,[string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$sourceOutput=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $workspaceRoot 'deliverables/Source'}
$stamp=Get-Date -Format 'yyyyMMdd_HHmmss'
$staging=Join-Path $workspaceRoot ".dev-cache/source-packages/MmdModern_$stamp"
if(Test-Path -LiteralPath $staging){throw 'Source staging directory already exists.'}
New-Item -ItemType Directory -Force -Path $staging,$sourceOutput|Out-Null
Push-Location $projectRoot
try{
 $files=@(& rg --files src tests fixtures scripts docs third_party tools native installer .github compatibility/d3d9-mme-host)+@('AGENTS.md','README.md','PRODUCT.md','DESIGN.md','Directory.Build.props','global.json','Mmd.Modern.slnx','NuGet.Config','THIRD_PARTY_NOTICES.md','.gitignore','.gitattributes')
 $manifest=@()
 foreach($relative in $files|Sort-Object -Unique){
  $source=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
  $target=[IO.Path]::GetFullPath((Join-Path $staging $relative))
  if(-not $target.StartsWith($staging+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw "Invalid source entry: $relative"}
  New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target))|Out-Null
  Copy-Item -LiteralPath $source -Destination $target
  $hash=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
  if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash){throw "Source copy differs: $relative"}
  $manifest+=[pscustomobject]@{path=$relative.Replace('\','/');bytes=(Get-Item -LiteralPath $source).Length;sha256=$hash}
 }
 $manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $staging 'source-manifest.json') -Encoding UTF8
 if($EvidenceDirectory){
  $sourceEvidence=[IO.Path]::GetFullPath($EvidenceDirectory)
  $evidenceTarget=Join-Path $staging 'verification';New-Item -ItemType Directory -Force -Path $evidenceTarget|Out-Null
  foreach($evidenceFile in Get-ChildItem -LiteralPath $sourceEvidence -File|Where-Object{$_.Extension -in '.json','.trx'}){Copy-Item -LiteralPath $evidenceFile.FullName -Destination (Join-Path $evidenceTarget $evidenceFile.Name)}
 }
 $zip=Join-Path $sourceOutput "MmdModern_${stamp}_Source.zip"
 Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal
 [pscustomobject]@{createdUtc=[DateTime]::UtcNow.ToString('o');archive=$zip;sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant();sourceFiles=$manifest.Count;moduleIndex='docs/SOURCE_INDEX.md';originalUserAssetsIncluded=$false;buildCachesIncluded=$false}|ConvertTo-Json|Set-Content -LiteralPath ($zip+'.json') -Encoding UTF8
 Write-Output $zip
}finally{Pop-Location}
