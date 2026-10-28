using System.Text.Json.Serialization;
using Yuehua.Models;

namespace Yuehua.Services;

/// <summary>
/// 源生成 JSON 上下文（NativeAOT 友好，避免运行时反射）。
/// 标注多态序列化依赖 Annotation 基类上的 [JsonDerivedType]。
/// AllowNamedFloatingPointLiterals 是保险丝：万一还有派生几何带出 ±∞/NaN，
/// 也只是写成 "Infinity" 而不是整段历史快照 / 侧车保存直接抛异常。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(AnnotationSidecar))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<AnnotationLayer>))]
[JsonSerializable(typeof(AnnotationLayer))]
[JsonSerializable(typeof(Annotation))]
[JsonSerializable(typeof(RectangleAnnotation))]
[JsonSerializable(typeof(EllipseAnnotation))]
[JsonSerializable(typeof(LineAnnotation))]
[JsonSerializable(typeof(ArrowAnnotation))]
[JsonSerializable(typeof(TextAnnotation))]
[JsonSerializable(typeof(FreehandAnnotation))]
[JsonSerializable(typeof(DPoint))]
[JsonSerializable(typeof(List<DPoint>))]
[JsonSerializable(typeof(MosaicAnnotation))]
[JsonSerializable(typeof(BlurAnnotation))]
internal sealed partial class JsonContext : JsonSerializerContext
{
}
