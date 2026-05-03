---
name: dotnet-file-based-apps
description: >
  .NET 10 の File-based apps（単一 .cs ファイルを dotnet で直接ビルド/実行する形式）を作成・修正するときに使う。
  File-based apps / File-based App / run C# file directly / #:-directives / #:package / dotnet run file.cs / dotnet publish file.cs の話題に反応してロードする。
  NuGet 参照は必ず「#:package」で書き、.csproj は作らない（作成提案もしない）。
argument-hint: "[target.cs] [やりたいこと] [必要なNuGetパッケージ]"
user-invokable: true
disable-model-invocation: false
---

# .NET 10 File-based apps Skill

## 0) 参照元（最重要）
- 主要な文法・ディレクティブ・CLI 操作は、公式ドキュメントに集約されている  
  https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps

このスキルは「上記ドキュメントの内容を逸脱しない」ことを第一優先にする。

## 1) 絶対に守るルール（よくミスる所）
### 1-1) NuGet パッケージ参照は `#:package` だけ
- 例（OK）:
  - `#:package Newtonsoft.Json`
  - `#:package Serilog@3.1.1`
  - `#:package Spectre.Console@*`
- 禁止（混同しやすいので絶対に出さない）:
  - csproj の `<PackageReference .../>`
  - `#r "nuget: ..."`（C# scripting系の書き方）
  - `dotnet add package ...` を前提にした説明

### 1-2) `.csproj` は作らない
- File-based apps は「単一 .cs ファイルの先頭に `#:` ディレクティブを置く」方式
- 出力として `.csproj` の生成・追加を提案しない
- ただし、ユーザーが「プロジェクト形式へ変換したい」と明示した場合に限り、公式の変換コマンドの存在を示す（勝手に変換はしない）

## 2) 正しい実行・ビルド確認のやり方
### 2-1) 実行（正）
- 基本:
  - `dotnet run file.cs`
- カレントに既存の `.csproj` がある場合の誤動作回避（優先推奨）:
  - `dotnet run --file file.cs`

### 2-2) 動かさずに「ビルド確認だけ」したい（正）
- `dotnet publish file.cs`
  - 実行はしない。ビルド/発行が通ることを確認する用途に使う

## 3) 生成するときの作法（Copilot の手順）
1. まず「対象は File-based app で、csproj は作らない」ことを宣言してから作業する
2. 生成物は原則 **1ファイル（target.cs）** にまとめる
3. `#:` ディレクティブは **C# ファイルの先頭**にまとめて配置する
4. NuGet が必要なら必ず `#:package` を使い、可能ならバージョンも明示して再現性を上げる
5. ユーザーの希望がなければ、実行コマンドは `dotnet run --file target.cs` を提示する
6. 「ビルド確認だけ」と言われたら `dotnet publish target.cs` を提示する

## 4) ミニ雛形（例）
> 必要に応じてパッケージ行を増減してよい（ただし形式は `#:package` 固定）

#:property TargetFramework=net10.0
// #:package Spectre.Console@*

using System;

Console.WriteLine("Hello, file-based app!");