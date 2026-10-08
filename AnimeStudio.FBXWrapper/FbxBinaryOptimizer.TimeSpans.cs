using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeStudio;

public static partial class FbxBinaryOptimizer
{
    private const double FbxTicksPerSecond = 46186158000d;

    private static int WriteAnimationTimeSpans(Document document, Node objects, IReadOnlyList<ImportedKeyframedAnimation> animations)
    {
        var stacks = objects.Children.Where(n => n.Name == "AnimationStack")
            .ToDictionary(n => n.Properties[1].StringValue.Split('\0')[0], StringComparer.Ordinal);
        var byId = objects.Children.Where(n => n.Properties.FirstOrDefault()?.Type == 'L').ToDictionary(n => n.Properties[0].Integer);
        var edges = document.Nodes.FirstOrDefault(n => n.Name == "Connections")?.Children
            .Where(n => n.Name == "C" && n.Properties.Count >= 3 && n.Properties[1].Type == 'L' && n.Properties[2].Type == 'L')
            .GroupBy(n => n.Properties[2].Integer).ToDictionary(g => g.Key, g => g.Select(n => n.Properties[1].Integer).ToArray())
            ?? new Dictionary<long, long[]>();
        var takes = document.Nodes.FirstOrDefault(n => n.Name == "Takes");
        int updated = 0;
        for (var i = 0; i < animations.Count; i++)
        {
            var animation = animations[i];
            var name = animation.Name ?? "Take" + i;
            if (!stacks.TryGetValue(name, out var stack) ||
                !TryTimeSpan(animation, CurveTimeSpan(stack.Properties[0].Integer, byId, edges), out var start, out var stop)) continue;
            var properties = stack.Child("Properties70");
            if (properties == null)
            {
                properties = MakeNode("Properties70"); properties.Terminator = true;
                stack.Children.Add(properties); stack.Terminator = true;
            }
            foreach (var pair in new[] { ("LocalStart", start), ("LocalStop", stop), ("ReferenceStart", start), ("ReferenceStop", stop) })
            {
                var property = properties.Children.FirstOrDefault(n => n.Name == "P" && n.Properties.FirstOrDefault()?.StringValue == pair.Item1);
                if (property == null)
                {
                    property = MakeNode("P", TextProperty(pair.Item1), TextProperty("KTime"), TextProperty("Time"), TextProperty(""), TimeProperty(pair.Item2));
                    properties.Children.Add(property);
                }
                else if (property.Properties.Count == 5) property.Properties[4] = TimeProperty(pair.Item2);
                else throw new InvalidDataException("Unexpected FBX animation time property: " + pair.Item1);
            }
            if (takes == null)
            {
                takes = MakeNode("Takes"); takes.Terminator = true;
                takes.Children.Add(MakeNode("Current", TextProperty(""))); document.Nodes.Add(takes);
            }
            var take = takes.Children.FirstOrDefault(n => n.Name == "Take" && n.Properties.FirstOrDefault()?.StringValue == name);
            if (take == null)
            {
                take = MakeNode("Take", TextProperty(name)); take.Terminator = true;
                take.Children.Add(MakeNode("FileName", TextProperty(name + ".tak"))); takes.Children.Add(take);
            }
            foreach (var field in new[] { "LocalTime", "ReferenceTime" })
            {
                var range = take.Child(field);
                if (range == null) { range = MakeNode(field); take.Children.Add(range); take.Terminator = true; }
                range.Properties = new List<Property> { TimeProperty(start), TimeProperty(stop) };
            }
            updated++;
        }
        return updated;
    }

    private static (long Start, long Stop)? CurveTimeSpan(long stack, Dictionary<long, Node> objects, Dictionary<long, long[]> incoming)
    {
        var work = new Stack<long>(); var visited = new HashSet<long>(); work.Push(stack);
        long first = long.MaxValue, last = long.MinValue;
        while (work.TryPop(out var id))
        {
            if (!visited.Add(id) || !objects.TryGetValue(id, out var node)) continue;
            if (node.Name == "AnimationCurve")
            {
                var times = node.Array("KeyTime", 'l');
                if (times?.Count > 0)
                {
                    var data = times.DecodeArray();
                    for (var at = 0; at < data.Length; at += 8)
                    {
                        var time = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(at));
                        first = Math.Min(first, time); last = Math.Max(last, time);
                    }
                }
            }
            if (incoming.TryGetValue(id, out var children))
                foreach (var child in children)
                    if (objects.TryGetValue(child, out var item) && item.Name is "AnimationLayer" or "AnimationCurveNode" or "AnimationCurve") work.Push(child);
        }
        return first == long.MaxValue ? null : (first, last);
    }

    private static bool TryTimeSpan(ImportedKeyframedAnimation animation, (long Start, long Stop)? curves, out long start, out long stop)
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        if (animation.StartTime.HasValue && animation.StopTime.HasValue &&
            double.IsFinite(animation.StartTime.Value) && double.IsFinite(animation.StopTime.Value) && animation.StopTime >= animation.StartTime)
        {
            first = animation.StartTime.Value; last = animation.StopTime.Value;
        }
        else if (!curves.HasValue)
        {
            void Include(IEnumerable<float> times)
            {
                foreach (var time in times) if (float.IsFinite(time)) { first = Math.Min(first, time); last = Math.Max(last, time); }
            }
            foreach (var track in animation.TrackList ?? new List<ImportedAnimationKeyframedTrack>())
            {
                Include(track.Scalings.Select(k => k.time)); Include(track.Rotations.Select(k => k.time));
                Include(track.EulerRotations.Select(k => k.time)); Include(track.Translations.Select(k => k.time));
                if (track.BlendShape != null) Include(track.BlendShape.Keyframes.Select(k => k.time));
            }
        }
        start = stop = 0;
        var declared = double.IsFinite(first) && double.IsFinite(last);
        if (!declared && !curves.HasValue) return false;
        if (declared)
        {
            start = checked((long)Math.Round(first * FbxTicksPerSecond));
            stop = checked((long)Math.Round(last * FbxTicksPerSecond));
        }
        if (curves.HasValue)
        {
            // Some source clips have a terminal sample beyond m_StopTime. The range must
            // include every exported key, including that last frame, without modifying keys.
            start = declared ? Math.Min(start, curves.Value.Start) : curves.Value.Start;
            stop = declared ? Math.Max(stop, curves.Value.Stop) : curves.Value.Stop;
        }
        if (float.IsFinite(animation.SampleRate) && animation.SampleRate > 0)
        {
            // Float timestamps can sit just below an integer frame. Expand outwards only,
            // never truncate the final key to obtain a prettier frame number.
            var endFrame = stop / FbxTicksPerSecond * animation.SampleRate;
            var frame = Math.Round(endFrame);
            if (Math.Abs(endFrame - frame) < 0.0001)
                stop = Math.Max(stop, checked((long)Math.Round(frame / animation.SampleRate * FbxTicksPerSecond)));
        }
        return true;
    }

    private static Node MakeNode(string name, params Property[] properties) => new()
    { NameBytes = Encoding.UTF8.GetBytes(name), Properties = properties.ToList() };
    private static Property TextProperty(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)'S'); writer.Write(bytes.Length); writer.Write(bytes);
        return new Property { Encoded = stream.ToArray() };
    }
    private static Property TimeProperty(long value)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((byte)'L'); writer.Write(value);
        return new Property { Encoded = stream.ToArray() };
    }
}
