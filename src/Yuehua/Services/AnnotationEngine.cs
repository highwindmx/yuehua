using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Yuehua.Models;

namespace Yuehua.Services;

/// <summary>历史记录中的一步：一个完整的状态快照 + 一句人话标签。</summary>
public sealed class HistoryStep
{
    public string Label { get; init; } = "";
    public string Json { get; init; } = "";
}

/// <summary>
/// 标注引擎：管理图层、当前图层、历史记录（撤销 / 重做 / 任意跳转）。
///
/// 历史采用「线性快照」模型：<c>_history[0]</c> 是打开图片时的初始状态，每次改动都会在
/// 末尾追加一步（同时丢弃之前撤销掉的“未来分支”），<c>_cursor</c> 指向当前所处的一步。
/// 撤销 = 光标前移，重做 = 光标后移，点历史列表里的任意一步 = 直接跳过去。
///
/// 之所以不用「两个栈」，是因为线性历史能同时满足三件事：无限步撤销、可视化历史列表、
/// 任意步跳转 —— 且快照用 JSON 深拷贝（NativeAOT 安全，不依赖反射）。
/// </summary>
public class AnnotationEngine
{
    /// <summary>历史最多保留的步数（超出后从最早的一步开始丢弃）。</summary>
    public const int MaxHistory = 60;

    private readonly List<AnnotationLayer> _layers = new();
    private readonly List<HistoryStep> _history = new();
    private int _nextLayerId = 1;
    private int _currentLayerId;
    private int _cursor;

    public AnnotationEngine()
    {
        AddLayerCore("标注层 1");
        ResetHistory();
    }

    public IReadOnlyList<AnnotationLayer> Layers => _layers;
    public IReadOnlyList<HistoryStep> History => _history;

    /// <summary>当前状态在历史中的位置（0 起）。</summary>
    public int Cursor => _cursor;

    public int CurrentLayerId
    {
        get => _currentLayerId;
        set
        {
            if (_layers.Exists(l => l.Id == value))
                _currentLayerId = value;
        }
    }

    public AnnotationLayer CurrentLayer
        => _layers.Find(l => l.Id == _currentLayerId) ?? _layers[0];

    // ==================== 图层 ====================

    private AnnotationLayer AddLayerCore(string? name)
    {
        var layer = new AnnotationLayer
        {
            Id = _nextLayerId,
            Name = name ?? $"标注层 {_nextLayerId}",
        };
        _nextLayerId++;
        _layers.Add(layer);
        _currentLayerId = layer.Id;
        return layer;
    }

    public AnnotationLayer NewLayer(string? name = null, string label = "新建图层")
    {
        var layer = AddLayerCore(name);
        Commit(label);
        return layer;
    }

    public void RemoveLayer(int layerId, string label = "删除图层")
    {
        if (_layers.Count <= 1) return; // 至少保留一个图层
        _layers.RemoveAll(l => l.Id == layerId);
        if (!_layers.Exists(l => l.Id == _currentLayerId))
            _currentLayerId = _layers[0].Id;
        Commit(label);
    }

    public void SetLayerVisible(int layerId, bool visible, string label = "显示 / 隐藏图层")
    {
        var l = _layers.Find(x => x.Id == layerId);
        if (l == null || l.Visible == visible) return;
        l.Visible = visible;
        Commit(label);
    }

    public void SetLayerBake(int layerId, bool bake, string label = "烙印设置")
    {
        var l = _layers.Find(x => x.Id == layerId);
        if (l == null || l.BakeIntoExport == bake) return;
        l.BakeIntoExport = bake;
        Commit(label);
    }

    // ==================== 标注 ====================

    public void AddAnnotation(Annotation a, string? label = null)
    {
        a.LayerId = _currentLayerId;
        CurrentLayer.Items.Add(a);
        Commit(label ?? ("添加 " + DescribeType(a)));
    }

    public void RemoveAnnotation(Guid id, string label = "删除标注")
    {
        foreach (var l in _layers)
        {
            var it = l.Items.Find(x => x.Id == id);
            if (it != null) { l.Items.Remove(it); break; }
        }
        Commit(label);
    }

    public void ClearLayer(int layerId, string label = "清空图层")
    {
        var l = _layers.Find(x => x.Id == layerId);
        if (l == null || l.Items.Count == 0) return;
        l.Items.Clear();
        Commit(label);
    }

    /// <summary>标注类型的短中文名，用于历史标签。</summary>
    public static string DescribeType(Annotation a) => a switch
    {
        RectangleAnnotation => "矩形",
        EllipseAnnotation => "椭圆",
        LineAnnotation => "直线",
        ArrowAnnotation => "箭头",
        TextAnnotation => "文字",
        FreehandAnnotation => "画笔",
        MosaicAnnotation => "马赛克",
        BlurAnnotation => "模糊",
        _ => "标注",
    };

    // ==================== 历史 ====================

    public bool CanUndo => _cursor > 0;
    public bool CanRedo => _cursor < _history.Count - 1;
    public bool CanJump => _history.Count > 1;

    public void Undo()
    {
        if (_cursor <= 0) return;
        RestoreAt(_cursor - 1);
    }

    public void Redo()
    {
        if (_cursor >= _history.Count - 1) return;
        RestoreAt(_cursor + 1);
    }

    /// <summary>直接跳到历史中的某一步（用于历史列表点击）。</summary>
    public void JumpTo(int index)
    {
        if (index < 0 || index >= _history.Count || index == _cursor) return;
        RestoreAt(index);
    }

    /// <summary>载入一张图片自带的标注：状态与历史都从这一步重新开始。</summary>
    public void LoadLayers(List<AnnotationLayer> layers)
    {
        _layers.Clear();
        _nextLayerId = 1;
        foreach (var l in layers)
        {
            _layers.Add(l);
            if (l.Id >= _nextLayerId) _nextLayerId = l.Id + 1;
        }
        if (_layers.Count == 0) AddLayerCore(null);
        _currentLayerId = _layers[0].Id;
        ResetHistory();
    }

    private void ResetHistory()
    {
        _history.Clear();
        _history.Add(new HistoryStep { Label = "打开图片", Json = SerializeLayers() });
        _cursor = 0;
    }

    private void RestoreAt(int index)
    {
        var json = _history[index].Json;
        var restored = JsonSerializer.Deserialize<List<AnnotationLayer>>(
            json, JsonContext.Default.ListAnnotationLayer) ?? new List<AnnotationLayer>();

        _layers.Clear();
        _layers.AddRange(restored);
        _nextLayerId = 1;
        foreach (var l in _layers)
            if (l.Id >= _nextLayerId) _nextLayerId = l.Id + 1;

        if (!_layers.Exists(l => l.Id == _currentLayerId))
            _currentLayerId = _layers.Count > 0 ? _layers[0].Id : 0;
        if (_layers.Count == 0)
        {
            AddLayerCore(null);
        }

        _cursor = index;
    }

    /// <summary>把「当前状态」记为新的一步，并丢弃光标之后的旧分支。</summary>
    private void Commit(string label)
    {
        if (_cursor < _history.Count - 1)
            _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);

        _history.Add(new HistoryStep { Label = label, Json = SerializeLayers() });
        _cursor = _history.Count - 1;

        if (_history.Count > MaxHistory)
        {
            var drop = _history.Count - MaxHistory;
            _history.RemoveRange(0, drop);
            _cursor -= drop;
            if (_cursor < 0) _cursor = 0;
        }
    }

    private string SerializeLayers()
        => JsonSerializer.Serialize(_layers, JsonContext.Default.ListAnnotationLayer);
}
