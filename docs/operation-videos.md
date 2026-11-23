# SSCapture 操作風景動画

撮影中にCtrl・Shift・Alt・Winを押したときの動きを、既定のキー割り当てで実機を操作して録画しました。

- 背景は録画用の架空の画面です。実在のアプリや作業中の画面ではありません。
- 左上は操作の名前と説明、左下のオレンジの帯は押しているキーとマウスボタンです。
- 複数のモニターが必要な操作（Shiftでモニターをまたぐ・移る操作）と、固定サイズでのShift＋矢印は含みません。
- キーの割り当ては設定の「ショートカット」で変更できます。一覧は[使い方の「起動とショートカット」](usage.md#起動とショートカット)にあります。

## 撮影範囲を決める前のキー

### 撮影前に Shift＋左クリック：ポインタのあるモニター全体を撮影

![Shiftを押すとポインタのあるモニターが明るくなり、左クリックでモニター全体を撮影する](images/operations/shift-monitor.gif)

### 撮影前に Ctrl を押しながらクリック：ボタンや入力欄を1つだけ撮影

![Ctrlを押しながらポインタを入力欄・チェックボックス・ボタンへ動かし、ボタンをクリックして撮影する](images/operations/ctrl-control.gif)

### 撮影前に Ctrl を押しながら左ドラッグ：通ったコントロールを1枚にまとめる

![Ctrlを押しながら入力欄からボタンまでドラッグし、通過したコントロールを元の位置関係のまま合成する](images/operations/ctrl-controls-drag.gif)

### 撮影前に Win を押しながらクリック：ポインタの下のウィンドウを撮影

![Winを押している間はポインタの下のウィンドウが明るくなり、クリックでそのウィンドウを撮影する](images/operations/win-window.gif)

### 撮影前に Alt を短く押す：今回の撮影だけウィンドウスナップをON／OFF

![ポインタを動かさずにAltを短く押すとウィンドウスナップがONになり、キーを離したままでも窓の角に吸着する](images/operations/alt-tap-snap.gif)

## ドラッグ中のキー

### ドラッグ中に Win：押したときの縦横比を保つ

![矩形をドラッグ中にWinを押すと、そのときの縦横比を保ったまま範囲が広がる](images/operations/drag-win-aspect.gif)

### ドラッグ中に Ctrl：起点を中心に四方へ広げる

![矩形をドラッグ中にCtrlを押すと、ドラッグの起点を中心に範囲が四方へ広がる](images/operations/drag-ctrl-center.gif)

### ドラッグ中に Ctrl＋Shift：起点を中心に、縦横比を保って広げる

![矩形をドラッグ中にCtrlとShiftを押すと、起点を中心に縦横比を保って範囲が広がる](images/operations/drag-ctrl-shift.gif)

### ドラッグ中に Alt＋Shift：高さを固定して横だけ変える

![矩形をドラッグ中にAltとShiftを押すと、ポインタを上下に動かしても高さは変わらず、幅だけが変わる](images/operations/drag-alt-shift.gif)

### ドラッグ中に Alt＋Ctrl：幅を固定して縦だけ変える

![矩形をドラッグ中にAltとCtrlを押すと、ポインタを左右に動かしても幅は変わらず、高さだけが変わる](images/operations/drag-alt-ctrl.gif)

### ドラッグ中に Win＋Alt：幅と高さをグリッド間隔にそろえる

![矩形をドラッグ中にWinとAltを押すと、幅と高さが20px単位で変わる](images/operations/drag-win-alt-grid.gif)

### Alt を押しながらドラッグ：ウィンドウやモニターの端に吸着させる

![Altを押しながら資料の窓の角の近くでドラッグを始めると、始点と終点が窓の角に吸着する](images/operations/drag-alt-snap.gif)

動画の作り直し方は[ビルド手順の「READMEの撮影」](DEVELOPERS_GUIDE.md#readmeの撮影)にあります。
