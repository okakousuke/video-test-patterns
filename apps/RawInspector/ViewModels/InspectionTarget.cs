using RawInspector.Decoding;

namespace RawInspector.ViewModels;

/// <summary>
/// 調べる相手です。RAWと、そのときの読み方をひとまとめにします。
///
/// 別々に渡せるようにすると、あるRAWの数字を別の条件の説明と一緒に出せてしまいます。
/// 分布の窓も比較の窓も、出した数字が「何をどう読んだ結果か」を言い続ける必要があるので、
/// 対で持ち回ります。
/// </summary>
/// <param name="Title">画面に出す名前です。ファイル名と主要な条件を含みます。</param>
public sealed record InspectionTarget(RawImage Image, string Title, PreviewRenderOptions Options);
