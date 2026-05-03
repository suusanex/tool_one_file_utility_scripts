---
name: coverage-gap-resolution
description: "implementation-coverage-of-integration-test の未対応項目を起点に、Plan を実装の唯一の基準として必要な設計・実装・UnitTest を追加し、coverage 文書の状態を更新する"
# Copyright (c) 2026 suusanex (GitHub UserName)
# SPDX-License-Identifier: CC-BY-4.0
# License: https://creativecommons.org/licenses/by/4.0/
# Source: https://github.com/suusanex/coding_agent_plan_and_verify_process
---

## 位置づけ（このフェーズの意味）
- このフェーズは、`integration-test-verification-implementation` が残した未対応・保留項目を解決するための **Plan 準拠の設計・実装補完フェーズ** である。
- 目的は、`plans/<ticket-or-slug>.md` に書かれた要求・設計・ランタイム挙動・DoD に照らして、`plans/<ticket-or-slug>-implementation-coverage-of-integration-test.md` に記録された未解決 ID を前進させること。
- **coverage 文書は作業台帳であり、仕様の源泉ではない。** 実装の正否は coverage 文書の都合ではなく、Plan に書かれた意図を満たしているかで判断する。
- `integration-test-points` 文書は「何を確認するか」を示す。**何をどう実装すべきかは、Plan と既存コードの文脈から判断する。**
- 既存のブラックボックス観点 ID を起点に進める。**ID を増減・再採番しない。**

このフェーズは **coverage 文書の行を埋めるための穴埋めフェーズではない**。目的は「未実装部分を何でもよい形で埋める」ことではなく、**Plan に対して欠けている Implementation を補うこと** である。

---

## 前提（入力）
必須入力：
- `plans/<ticket-or-slug>.md`
- `plans/<ticket-or-slug>-implementation-coverage-of-integration-test.md`

対応する参照入力：
- `plans/<ticket-or-slug>-integration-test-points.md`
- 対象アプリケーションの実装コードと UnitTest プロジェクト
- Plan が参照する runtime-evidence / architecture / ADR / 関連ドキュメント（存在する場合）

`<ticket-or-slug>` は、**coverage 文書のファイル名からそのまま導出すること**。
例：
- 入力: `plans/plan-graph-github-projection-mvp-implementation-coverage-of-integration-test.md`
- 対応 Plan: `plans/plan-graph-github-projection-mvp.md`
- 対応観点: `plans/plan-graph-github-projection-mvp-integration-test-points.md`

注意：
- coverage 文書に書かれた ID と、integration-test-points 文書の ID を対応づけて進める。
- **coverage 文書に存在しない新規 ID を勝手に作らない。**
- coverage 文書の行構造・見出し・表形式は、既存内容を尊重して更新する。
- CI で実 OS（レジストリ / SetupAPI / サービス / ドライバ / デバイス等）を書き換えるテストは追加しない。

---

## このフェーズで扱う対象
優先対象は、coverage 文書上で次のいずれかになっている ID：
- `NotImplementedOrMismatch`
- `ManualOnly`
- `RecordedButSkipped`

既に `Automated` になっている ID は原則として対象外。ただし、次のいずれかに当てはまる場合のみ再確認してよい。
- coverage 文書上のテスト参照が欠けている
- テスト名や ID 対応が壊れている
- 今回の修正の影響で関連テストの更新が必要
- Plan を読むと、既存実装が **観点は満たしているように見えても Plan の意図を外している** と判断できる

---

## 成果物（出力）
必須：
- Plan の意図に沿って追加・更新した設計・実装コード
- 追加・更新した UnitTest コード
- 更新済みの `plans/<ticket-or-slug>-implementation-coverage-of-integration-test.md`
  - 各対象 ID について、少なくとも次を最新化すること
    - 状態（`Automated` / `RecordedButSkipped` / `ManualOnly` / `NotImplementedOrMismatch`）
    - 対応テストの FullyQualifiedName（またはファイル / メソッド）
    - 判定理由の要約

推奨：
- 今回状態が変わった ID の一覧
- まだ未解決のまま残る ID の短いサマリ
- 設計不足 / 実装不足 / テスト容易化不足 / 外部依存 / 人間判断待ち などの理由分類
- **Plan 上のどの要求・挙動・DoD に対応した実装か** の短いサマリ

---

## 最重要原則
### 1) Plan を実装の唯一の基準にする
- **最初に Plan を読む。** coverage 文書や points 文書だけで実装方針を決めない。
- 各 ID について、少なくとも次を Plan から確認する。
  - その観点が対応する要求・期待挙動
  - 関連するランタイムシナリオ / エラーパス
  - 関連する設計境界・責務分割・外部依存
  - Definition of Done に含まれる条件
- Plan に「GitHub OAuth」「SQLite」「外部 API」「永続化」「handoff packet」などの外部リソース用語がある場合、**それは実装上の意味を持つ制約であり、勝手に別物へ置き換えてはならない。**

### 2) coverage 文書は作業台帳であり、要求の代用品ではない
- このフェーズの対象決定は coverage 文書を基準にしてよい。
- ただし、**対象 ID の解決内容は必ず Plan に照らして決める**。
- coverage 文書は「何が残っているか」を示すだけで、正しい実装内容そのものは教えない。

### 3) points 文書は「確認観点」であり、「実装仕様」ではない
- `integration-test-points` 文書は、対象 ID の観測観点や検証対象を確認するために使う。
- そこに現れる語句をつまみ食いして、**表面的に満たしただけのダミー実装** を作ってはいけない。
- 例：Plan が「AI を用いて文字列を分割する」と言っているなら、`string.Split(...)` だけで分割する実装は、たとえ interface を満たしても本件の実装完了ではない。

### 4) 「穴を埋める」のではなく、「Plan に対する Implementation を補う」
- このフェーズの目的は、抜けているクラスやメソッドを何でもよい形で埋めることではない。
- **Plan が要求する振る舞い・境界・技術選択・DoD を満たす Implementation を追加すること** が目的である。
- interface を満たすだけ、値を返すだけ、空でないだけ、固定文字列を返すだけ、局所 heuristics でそれっぽく見せるだけの実装は、Plan を満たさない限り「解決」ではない。

---

## 重要ルール
### 1) ID 対応を崩さない
- 既存 ID をそのまま使う。
- テスト名または属性には、対象 ID を含める。
  - 例: `UT_IT_005__GitHub投影本文を生成する`
- 1 つのテストが複数 ID を兼ねる場合でも、coverage 文書側では各 ID ごとに個別に記録する。

### 2) Plan に書かれた「方式・境界・外部依存」を消さない
- Plan が特定の方式や境界を要求している場合、それを **別の簡易ロジックにすり替えてはならない**。
- 次のような置換は禁止：
  - 外部 API / SDK / 永続化 / 認証 / 投影 / ハンドオフが要求されているのに、単純な in-memory / local heuristic / fixed return で代替する
  - 例外経路や状態遷移が要求されているのに、常に成功するだけの実装にする
  - 実副作用が要求されているのに、副作用の無いダミー実装で済ませる
- ただし、Plan または既存設計に **明示的な dev/test fallback** がある場合は、その境界を壊さない範囲で利用してよい。

### 3) bounded な新規実装追加は許可する
- 既存クラスが無いこと自体は停止理由にしない。
- 対象 ID を Plan 準拠で前進させるために必要なら、**bounded な新規クラス / adapter / endpoint / DI 配線 / mapping / DTO / repository / service の追加** を行ってよい。
- ただし、目的は gap 解消ではなく **Plan の Implementation 補完** である。
- 包括的な再設計、大規模リファクタリング、周辺最適化には広げない。

### 4) まず「Plan 準拠で実装可能か」を判断する
- `NotImplementedOrMismatch` の中には、単純な実装漏れだけでなく、設計不足・モデル不足・観点不整合・Plan 解釈不足も含まれる。
- まず次のどれに近いかを判断する。
  - **Plan準拠の実装不足**: 既存設計または bounded な追加で Plan に沿って対応できる
  - **Plan準拠の設計不足**: 状態モデル、永続化境界、責務分割、adapter などの追加が必要
  - **テスト不足**: 実装はおおむねあるが UnitTest の観測点や足場がない
  - **外部依存 / 自動化困難**: このフェーズで通常実行対象にしにくい
  - **人間判断が必要**: Plan だけでは正解が確定しない
- 可能な限り対象 ID に閉じた範囲で修正する。ただし **Plan の意味を壊してまで狭くしない**。

### 5) 実装前に「Plan 対応表」を頭の中で作る
各対象 ID について、実装前に少なくとも次を整理してから着手すること。
- 対応する ID
- Plan 上の対応要求 / シナリオ / DoD
- 実装すべき責務
- 触るべき境界（例：SDK adapter, repository, API endpoint, UI component, DI）
- 何をもって「ダミーではなく前進した」と言えるか

### 6) `Automated` 判定の前に「ダミー実装チェック」を必ず行う
次のいずれかに当てはまる場合、**実装を追加しても `Automated` にしてはならない**。必要なら `NotImplementedOrMismatch` を維持し、理由を具体化する。
- interface / abstract class の形だけ満たしている
- 実質的に固定値・空実装・no-op・string split / regex / in-memory heuristic などで、その場しのぎに観点を満たしているだけ
- Plan が要求する外部依存や境界を使わず、局所的な疑似ロジックに置き換えている
- Plan が要求する成功 / 失敗 / 状態遷移 / 副作用のうち、重要部分を黙って落としている
- 「テストが通る」以外に、その実装が Plan を満たす説明ができない

### 7) テストは観点だけでなく Plan の意図に対応させる
- テストは存在確認だけで終わらせない。
- 成功 / 例外 / 戻り値 / 状態変化 / 永続化結果 / 生成文面 / ハンドオフ内容など、対象観点に対応する意味のある結果を確認する。
- さらに、**Plan が要求する境界や方式が守られているか** も可能な範囲で確認する。
- ただし、完璧な E2E 再現が必要なら、その理由を残して `RecordedButSkipped` / `ManualOnly` に寄せる。

### 8) coverage 文書の更新は「結果反映」であり「願望記入」ではない
- `Automated` にするのは、実際に対応する UnitTest を追加・更新し、かつ **Plan の意図に沿った実装前進** がある場合に限る。
- `RecordedButSkipped` にするのは、対応テストが存在し、通常実行対象外にした理由が明確な場合に限る。
- `ManualOnly` / `NotImplementedOrMismatch` は、なぜ今そこに留めたのかが読めるように書く。
- **「見た目は埋まったが Plan 的には未実装」なら、正直に `NotImplementedOrMismatch` に残す。**

---

## 優先順位
1. 各未解決 ID を Plan 上の要求・挙動・DoD に結び付けて理解する
2. Plan 準拠で `Automated` に移せる ID から解決する
3. bounded な設計追加で解けるものは、対象を絞って実装とテストまで進める
4. 自動化困難なものは、理由を具体化して `RecordedButSkipped` / `ManualOnly` に整理する
5. Plan を満たさないダミー実装しか置けないなら、無理に埋めず `NotImplementedOrMismatch` の理由を更新して次の人間判断につなぐ

---

## 推奨手順
### Step 1. 対応する Plan / coverage / points を揃えて読む
- `plans/<ticket-or-slug>.md` を最初に読む。
- `plans/<ticket-or-slug>-implementation-coverage-of-integration-test.md` を読み、`NotImplementedOrMismatch` / `ManualOnly` / `RecordedButSkipped` の ID を一覧化する。
- `plans/<ticket-or-slug>-integration-test-points.md` を開き、対象 ID の観点内容を確認する。
- Plan が参照する runtime-evidence / architecture / ADR / 関連 docs があるなら必要範囲で読む。

### Step 2. 各 ID を Plan 上の要求・シナリオ・DoD に結びつける
- 各 ID について、次を短く整理する。
  - 対応する要求または期待挙動
  - 関連するシナリオ / エラーパス / 外部依存
  - 期待される境界・技術・責務
  - 既存実装の不足点
- coverage 文書の既存理由が曖昧な場合は、points 文書ではなく **Plan を基準に補正する**。

### Step 3. 各 ID を「Plan 準拠の解決方法」で分類する
- 各 ID について、次のどれに該当するかを判断する。
  - 少量の実装追加で Plan 準拠に対応可能
  - 小さな設計追加で Plan 準拠に対応可能
  - テスト足場の追加で対応可能
  - このフェーズでは通常実行対象にできない
  - まだ人間判断が必要
- 複数 ID が同じ設計不足にぶら下がっているなら、共通原因をまとめて扱ってよい。

### Step 4. 必要な設計・実装・UnitTest を追加する
- まず対象 ID を **Plan 準拠で前進させる** のに必要な最小範囲の設計・実装を加える。
- 続いて、その観点を確認する UnitTest を追加・更新する。
- 可能なら狭いスコープで確認し、テスト名・属性に ID を反映する。
- ただし、Plan が要求する本質的な機構を local heuristic で置き換えない。

### Step 5. `Automated` 更新前に「ダミー実装チェック」を行う
- 実装が Plan の要求する境界・方式・副作用・失敗パスを本当に担っているか確認する。
- 「空でない」「値を返す」「テストが通る」だけなら不十分。
- 実装が Plan の意図に届いていないと判断したら、`Automated` にせず `NotImplementedOrMismatch` の理由を更新する。

### Step 6. 結果に応じて coverage 文書を更新する
- 自動テストで確認でき、かつ Plan 準拠の実装前進がある ID は `Automated` に更新する。
- テストは用意したが通常実行対象外にしたものは `RecordedButSkipped` に更新する。
- 手動確認向けに残すものは `ManualOnly` に更新する。
- 未解決のまま残す場合は `NotImplementedOrMismatch` の理由を、Plan に照らしてより具体的に更新する。
- 対応テスト参照も忘れず更新する。

### Step 7. 最後に整合を確認する
- 今回対象にした ID について、coverage 文書の状態・理由・テスト参照が最新になっていることを確認する。
- points 文書の ID と coverage 文書の ID の対応が崩れていないことを確認する。
- 実装変更が入ったのに coverage 文書が古いまま、という状態で終えない。
- **Plan の重要要求が、今回の実装で黙って別物に置き換わっていないことを確認する。**

---

## 停止判断
次の条件では、無理に `Automated` を目指さず、coverage 文書に理由を更新して閉じる。
- 対応には大きな仕様再定義や広範囲の設計変更が必要
- 本来は integration / e2e / manual smoke で扱うべき観点だと判断できる
- 妥当な oracle を置けず、意味のある assert が作れない
- 実装を進めても人間判断がないと正解が定まらない
- 今回の対象 ID を超えて横断的な基盤工事が必要になった
- **Plan が要求する方式・外部依存・振る舞いを、このフェーズでは本物として実装できず、ダミー実装しか置けない**

この場合でも、対象 ID ごとに coverage 文書上の理由を具体化し、必要なら「次に必要な設計判断」または「不足している実装境界」を短く残す。

---

## 禁止事項
- coverage 文書の ID を勝手に追加・削除・再採番すること
- 対象 ID と無関係な最適化・大規模リファクタリングに広げること
- 実装だけ変更して coverage 文書を更新せず終えること
- 弱い assert で `Automated` 扱いにすること
- CI で実 OS / 実環境を書き換えるテストを追加すること
- 曖昧な理由のまま `NotImplementedOrMismatch` を据え置くこと
- **Plan に書かれた方式・境界・外部依存を、明示的な許可なく別物へすり替えること**
- **interface を満たしただけ / 空でないだけ / 固定値を返すだけの実装を「解決」とみなすこと**
- **points 文書のキーワードだけを拾って、Plan を読まずにそれっぽい実装を作ること**
- **「未実装部分を何でもいいから埋める」ことを目的化すること**

---

## 完了条件（DoD）
- 入力 coverage 文書で対象とした未解決 ID について、状態・理由・テスト参照が最新化されている。
- 解決できた ID には、対応する設計・実装・UnitTest の追加または更新がある。
- `Automated` に前進した ID では、**実装が Plan の要求・境界・主要挙動を実質的に満たしている**。
- Plan が要求する外部依存や技術境界がある場合、**それに対応する本物の実装境界（または少なくとも正しい adapter / wiring）が存在する**。存在しないなら未解決理由が明記されている。
- 解決できなかった ID も、前回より具体的な理由または次の判断材料が coverage 文書に残っている。
- points 文書の ID と coverage 文書の ID の対応が崩れていない。
- 実装・テスト・coverage 文書の 3 つが矛盾しない状態で終わっている。
- **Plan を読む第三者が、「これは穴埋めではなく Plan 実装になっている」と判断できる状態で終わっている。**

## 出力時のまとめ方
最後に、少なくとも次を短く報告すること。
- `Automated` に前進できた ID 一覧
- `RecordedButSkipped` / `ManualOnly` に整理した ID 一覧と理由
- `NotImplementedOrMismatch` のまま残った ID 一覧と更新理由
- 追加・更新した主な設計・実装
- 追加・更新した主なテスト
- **今回の実装が対応した Plan 上の要求 / シナリオ / DoD の要約**
- 人間が次に判断すべき項目
