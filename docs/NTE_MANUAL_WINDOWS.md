# UEExtractor NTE — uso e desenvolvimento manual no Windows

Este guia separa dois cenários diferentes:

1. usar o build já aprovado pela pipeline do NTE;
2. modificar e recompilar o código-fonte do UEExtractor.

> [!IMPORTANT]
> O UEExtractor é escrito em **C# sobre .NET 10**. Ele não é um projeto C++ e não usa Flutter. A carga de trabalho C++ do Visual Studio é necessária para os aplicativos Flutter Windows do launcher e do Studio, não para compilar este repositório.

## Cenário A — somente usar o extrator aprovado no NTE Translation Studio

Para usar o Studio e a pipeline você não precisa clonar ou compilar este repositório.

O build aprovado é:

```text
UEExtractor NTE 1.0.8.4.3
```

Download utilizado pela pipeline:

<https://github.com/MauricioIkeda/nte-ptbr-releases/releases/download/tools-ueextractor-nte-1.0.8.4.3/UEExtractor-NTE-1.0.8.4.3-win-x64.zip>

### Hash do ZIP

```text
1B6BDAA1906BDD9F01F6CE632904BAB4FA42C6B9373F1DCD5DAAE981DB4C189F
```

### Hash do `UEExtractor.exe`

```text
BC7B8D8AE365BC10402770068D77C90FA2E27C04026592C8A0DF93B98DA8472D
```

### Hash do `UEExtractor.dll`

```text
012CA72B3594F9688C94DA93D51023E6278CDABC91B8CBB6136D6A19B3D952B4
```

A pipeline também grava esses hashes em `nte.config.json` e bloqueia extração ou build quando os arquivos locais são diferentes.

### Instalação manual para a pipeline

Partindo da raiz de `nte-ptbr-automatic-translation`:

```powershell
New-Item -ItemType Directory -Force `
  -Path .\workspace\tools\downloads | Out-Null
New-Item -ItemType Directory -Force `
  -Path .\workspace\tools\ueextractor-nte | Out-Null

$Zip = ".\workspace\tools\downloads\UEExtractor-NTE-1.0.8.4.3-win-x64.zip"

Invoke-WebRequest `
  -Uri "https://github.com/MauricioIkeda/nte-ptbr-releases/releases/download/tools-ueextractor-nte-1.0.8.4.3/UEExtractor-NTE-1.0.8.4.3-win-x64.zip" `
  -OutFile $Zip
```

Confira o ZIP:

```powershell
(Get-FileHash -Algorithm SHA256 -LiteralPath $Zip).Hash
```

Extraia somente se o hash for idêntico ao valor aprovado:

```powershell
Expand-Archive `
  -LiteralPath $Zip `
  -DestinationPath .\workspace\tools\ueextractor-nte `
  -Force
```

Confira os binários:

```powershell
Get-FileHash `
  -Algorithm SHA256 `
  -LiteralPath .\workspace\tools\ueextractor-nte\UEExtractor.exe

Get-FileHash `
  -Algorithm SHA256 `
  -LiteralPath .\workspace\tools\ueextractor-nte\UEExtractor.dll
```

Não substitua esses arquivos por uma release aleatória do upstream. O build NTE contém correções específicas para leitura, patch e preservação estrutural do LOCRES.

## Cenário B — desenvolver ou recompilar o UEExtractor

### Ferramentas necessárias

| Ferramenta | Para que serve |
|---|---|
| Git | clonar este repositório e o submódulo CUE4Parse |
| .NET 10 SDK | restaurar pacotes e compilar o projeto C# `net10.0` |
| Visual Studio Community | IDE gráfica opcional para editar e depurar C# |
| VS Code + C# Dev Kit | alternativa leve ao Visual Studio |
| PowerShell | executar comandos, hashes e testes manuais |

Você pode compilar apenas com Git, .NET 10 SDK e um terminal. Visual Studio ou VS Code são editores opcionais.

## 1. Instalar o Git

Página oficial: <https://git-scm.com/download/win>

Instalação opcional pelo WinGet:

```powershell
winget install --exact --id Git.Git --source winget
```

Verifique:

```powershell
git --version
```

## 2. Instalar o .NET 10 SDK

Página oficial: <https://dotnet.microsoft.com/download/dotnet/10.0>

Instalação opcional pelo WinGet:

```powershell
winget install --exact --id Microsoft.DotNet.SDK.10 --source winget
```

Feche e abra novamente o terminal. Depois confirme:

```powershell
dotnet --version
dotnet --list-sdks
```

Deve existir uma linha começando com `10.0`.

O arquivo `UEExtractor/UEExtractor.csproj` declara:

```xml
<TargetFramework>net10.0</TargetFramework>
```

Um runtime isolado não substitui o SDK. Para modificar e compilar, instale o **SDK**.

## 3. Escolher uma IDE

### Opção 1 — Visual Studio Community

Página oficial: <https://visualstudio.microsoft.com/downloads/>

No Visual Studio Installer, selecione:

```text
Desenvolvimento para desktop com .NET
```

Essa é a carga apropriada para C#. Não é necessário selecionar desenvolvimento C++ apenas por causa do UEExtractor.

### Opção 2 — VS Code

Página oficial: <https://code.visualstudio.com/>

Instale as extensões:

```text
ms-dotnettools.csdevkit
ms-dotnettools.csharp
```

Pelo terminal:

```powershell
code --install-extension ms-dotnettools.csdevkit
code --install-extension ms-dotnettools.csharp
```

## 4. Clonar a fonte NTE aprovada

As correções usadas no build NTE 1.0.8.4.3 estão na branch:

```text
fix/nte-aes-submitkey
```

Ela usa `MauricioIkeda/CUE4Parse` como submódulo.

Crie sua pasta de projetos:

```powershell
$Projects = Join-Path $HOME "Documents\GitHub"
New-Item -ItemType Directory -Force -Path $Projects | Out-Null
Set-Location $Projects
```

Clone a branch com todos os submódulos:

```powershell
git clone `
  --branch fix/nte-aes-submitkey `
  --recurse-submodules `
  https://github.com/MauricioIkeda/UEExtractor.git

Set-Location .\UEExtractor
```

Confirme:

```powershell
git branch --show-current
git status --short
git submodule status --recursive
```

Resultado esperado para a branch:

```text
fix/nte-aes-submitkey
```

O status principal deve estar vazio. O submódulo não deve aparecer com prefixo `-`, que indicaria ausência de checkout.

### Recriar ou corrigir o submódulo

Caso tenha clonado sem `--recurse-submodules`:

```powershell
git submodule sync --recursive
git submodule update --init --recursive
```

O arquivo `.gitmodules` da branch NTE aponta para:

```text
https://github.com/MauricioIkeda/CUE4Parse.git
```

Não troque o submódulo por outra versão sem testar novamente extração, sidecar, patch LOCRES e hashes.

## 5. Restaurar dependências

Na raiz do repositório:

```powershell
dotnet restore .\UEExtractor\UEExtractor.csproj
```

Esse comando lê o `.csproj`, baixa os pacotes NuGet declarados e restaura também as referências necessárias ao build.

## 6. Compilar em Debug

```powershell
dotnet build `
  .\UEExtractor\UEExtractor.csproj `
  --configuration Debug
```

Use Debug durante investigação e depuração.

A saída começa em:

```text
UEExtractor\bin\Debug\net10.0\
```

## 7. Compilar em Release

```powershell
dotnet build `
  .\UEExtractor\UEExtractor.csproj `
  --configuration Release
```

A saída começa em:

```text
UEExtractor\bin\Release\net10.0\
```

Liste os arquivos gerados:

```powershell
Get-ChildItem `
  -LiteralPath .\UEExtractor\bin\Release\net10.0 `
  -File
```

Não copie apenas um arquivo até confirmar quais DLLs e arquivos de runtime o build atual requer.

## 8. Abrir na IDE

### VS Code

```powershell
code .
```

Abra o projeto:

```text
UEExtractor\UEExtractor.csproj
```

### Visual Studio

No Visual Studio, use:

```text
Arquivo > Abrir > Projeto/Solução
```

Selecione:

```text
UEExtractor\UEExtractor.csproj
```

O Visual Studio consegue abrir diretamente um `.csproj`; uma solution não é obrigatória.

## 9. Arquivos importantes para o NTE

Na branch aprovada, as áreas mais sensíveis incluem:

```text
UEExtractor\Localization\CLI_Processor.cs
UEExtractor\Localization\CUE4Parse\UnrealArchiveReader.cs
UEExtractor\Localization\Locres\LocresCompactWriter.cs
UEExtractor\Localization\Unreal\UnrealLocres.cs
CUE4Parse\
```

Responsabilidades:

- detectar e encaminhar argumentos específicos;
- ler a estrutura criptografada do LOCRES;
- preservar hashes, namespaces, chaves e ordem;
- escrever strings no formato esperado pelo NTE;
- integrar formatos específicos no CUE4Parse.

Uma pequena alteração nessas áreas pode produzir um arquivo que parece válido, mas é ignorado pelo jogo. Nunca valide apenas pelo exit code do programa.

## 10. Testar o executável

Confira a versão:

```powershell
.\UEExtractor\bin\Release\net10.0\UEExtractor.exe --version
```

Confira a ajuda:

```powershell
.\UEExtractor\bin\Release\net10.0\UEExtractor.exe --help
```

### Extração do NTE

O fluxo usado pela pipeline é equivalente a:

```powershell
.\UEExtractor.exe `
  "CAMINHO_PARA_HT" `
  "CAMINHO_DE_SAIDA\" `
  "--path=HT/Content/Localization/Game/en/game.locres" `
  "--version=GAME_NevernessToEverness" `
  "--extract-locres" `
  "--verbose"
```

A pasta `HT` fica dentro de:

```text
<PASTA_DO_NTE>\Client\WindowsNoEditor\HT
```

A AES key deve ser fornecida pela forma suportada pelo fluxo testado, sem ser adicionada ao Git.

### Patch do LOCRES

```powershell
.\UEExtractor.exe `
  "Game.locres" `
  "game_ptbr.csv" `
  "--version=GAME_NevernessToEverness" `
  "--verbose"
```

O resultado esperado é um arquivo:

```text
Game_patched.locres
```

O log deve informar um número significativo de traduções aplicadas. A pipeline bloqueia builds que aplicam poucas traduções.

## 11. Provas obrigatórias para uma nova versão NTE

Antes de substituir o build aprovado, valide:

1. o executável inicia e informa a versão correta;
2. a extração real do NTE termina sem travar;
3. são gerados CSV, LOCRES original e `.locreshashes`;
4. o banco da pipeline importa a extração;
5. o round-trip passa para todos os textos;
6. o patch preserva a contagem de entradas;
7. namespaces, hashes, chaves e ordem permanecem compatíveis;
8. milhares de traduções são aplicadas, não apenas dezenas;
9. o jogo carrega visualmente o LOCRES traduzido;
10. os hashes do novo ZIP, EXE e DLL são registrados na pipeline;
11. `Configurar-NTE.ps1`, `nte.config.example.json` e constantes Python são atualizados juntos;
12. uma recuperação em pasta limpa baixa exatamente o novo pacote.

## 12. Calcular os hashes

```powershell
Get-FileHash `
  -Algorithm SHA256 `
  -LiteralPath .\CAMINHO\UEExtractor.exe

Get-FileHash `
  -Algorithm SHA256 `
  -LiteralPath .\CAMINHO\UEExtractor.dll

Get-FileHash `
  -Algorithm SHA256 `
  -LiteralPath .\CAMINHO\UEExtractor-NTE-versao-win-x64.zip
```

Nunca copie um hash manualmente de um build diferente.

## 13. Trabalhar com branches

Não faça novas alterações diretamente na `main` nem na branch aprovada.

Atualize a branch-base desejada:

```powershell
git checkout fix/nte-aes-submitkey
git pull --ff-only
```

Crie uma branch de trabalho:

```powershell
git checkout -b fix/descricao-da-correcao
```

Antes do commit:

```powershell
git status --short
git diff --check
git diff
git submodule status --recursive
```

Caso o ponteiro do CUE4Parse tenha mudado, confirme que a alteração é intencional. Um submódulo modificado sem commit acessível pode tornar o repositório impossível de reconstruir em outro computador.

Commit e push:

```powershell
git add CAMINHOS_DESEJADOS
git commit -m "fix: descrição objetiva"
git push --set-upstream origin HEAD
```

## 14. O que precisa ser salvo antes de formatar

Se todo o código e o ponteiro do submódulo estiverem enviados ao GitHub, não é necessário copiar:

```text
bin\
obj\
.vs\
pacotes NuGet restaurados
```

Eles são recriados por `dotnet restore` e `dotnet build`.

Antes de formatar, confirme no repositório local:

```powershell
git status --short
git branch --show-current
git log -1 --oneline
git submodule status --recursive
git fetch origin
git rev-parse HEAD
git rev-parse "origin/$(git branch --show-current)"
```

O status deve estar vazio e os SHAs local/remoto da branch devem ser iguais.

## 15. Problemas comuns

### `dotnet` não é reconhecido

- confirme que instalou o SDK, não apenas o runtime;
- feche e reabra o terminal;
- execute `where.exe dotnet`;
- execute `dotnet --list-sdks`.

### CUE4Parse ausente

```powershell
git submodule sync --recursive
git submodule update --init --recursive
```

### O projeto compila, mas a pipeline rejeita o executável

O hash é diferente do build aprovado. Isso é esperado para uma nova compilação. Não altere os hashes da pipeline até concluir todas as provas de compatibilidade e decidir conscientemente substituir o build oficial.

### O LOCRES é criado, mas o jogo ignora

Isso geralmente indica incompatibilidade estrutural, hashes incorretos, namespace/chave alterados, origem errada ou patch aplicado sobre outro LOCRES. Compare a identidade da fonte e execute o fluxo completo da pipeline.

## Checklist final de desenvolvimento

```powershell
git --version
dotnet --version
dotnet --list-sdks
git branch --show-current
git submodule status --recursive
dotnet restore .\UEExtractor\UEExtractor.csproj
dotnet build .\UEExtractor\UEExtractor.csproj --configuration Release
```

Com esses itens aprovados, o código-fonte do UEExtractor NTE pode ser editado e recompilado sem depender das pastas do computador anterior.
