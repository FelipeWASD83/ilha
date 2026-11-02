using System;
using System.Numerics;
using Vortice.Direct2D1;
using Windows.UI.Composition;

namespace Ilha;

/// <summary>
/// Smooth swap of what a surface shows (a new song, a new Claude Code step) without rebuilding the scene: the old
/// content is drawn once into a temporary layer on top, slides out and fades, while the live layer (already redrawn
/// with the new content) slides in from the other side. Everything runs in the compositor; the temporary layer is
/// released when the batch ends or with the scene.
/// </summary>
internal static class ContentSwap
{
    /// <param name="live">The surface that already shows (or is about to show) the new content.</param>
    /// <param name="oldDrawing">Draws the content that is leaving.</param>
    /// <param name="direction">Where the old content goes (unit vector, e.g. (-1, 0) to the left, (0, -1) up).</param>
    /// <param name="distance">How far it travels, in DIP.</param>
    /// <param name="pop">Also shrink the old one and grow the new one with a slight overshoot (covers, icons).</param>
    public static void Play(Palco p, Cena cena, Superficie live, Action<ID2D1DeviceContext, Vector2> oldDrawing,
        Vector2 direction, float distance, int ms = 280, bool pop = false)
    {
        var c = p.C;
        SpriteVisual? liveVisual = null;
        foreach (var v in cena.Raiz.Children)
            if (v is SpriteVisual sv && ReferenceEquals(sv.Brush, live.Pincel)) { liveVisual = sv; break; }
        if (liveVisual == null) return;

        // the base offset is the one the scene set; a swap still running must not leave the live layer displaced
        liveVisual.StopAnimation("Offset");
        liveVisual.StopAnimation("Opacity");
        liveVisual.StopAnimation("Scale");
        var basePos = BaseOffset(liveVisual);
        liveVisual.Opacity = 1;
        liveVisual.Scale = Vector3.One;

        Superficie oldSurface;
        try
        {
            oldSurface = p.G.NovaSuperficie(live.TamanhoDip, (dc, t) =>
            {
                try { oldDrawing(dc, t); } catch (Exception) { }
            });
        }
        catch (Exception e) { Log.Escrever("troca animada: " + e.Message); return; }
        var oldVisual = c.CreateSpriteVisual();
        oldVisual.Size = liveVisual.Size;
        oldVisual.Offset = basePos;
        oldVisual.Brush = oldSurface.Pincel;
        cena.Raiz.Children.InsertAbove(oldVisual, liveVisual);
        // registered with the scene: a scene released mid-swap releases the temporary layer too
        cena.Superficies.Add(oldSurface);

        var center = new Vector3(liveVisual.Size.X / 2, liveVisual.Size.Y / 2, 0);
        oldVisual.CenterPoint = center;
        liveVisual.CenterPoint = center;
        var shift = new Vector3(direction.X, direction.Y, 0) * (distance * p.Escala);
        var easeIn = c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var easeOut = c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var outDuration = TimeSpan.FromMilliseconds(ms * 0.7);
        var inDuration = TimeSpan.FromMilliseconds(ms);
        var inDelay = TimeSpan.FromMilliseconds(ms * 0.12);

        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var oldOffset = c.CreateVector3KeyFrameAnimation();
        oldOffset.InsertKeyFrame(0f, basePos);
        oldOffset.InsertKeyFrame(1f, basePos + shift, easeIn);
        oldOffset.Duration = outDuration;
        oldVisual.StartAnimation("Offset", oldOffset);
        var oldOpacity = c.CreateScalarKeyFrameAnimation();
        oldOpacity.InsertKeyFrame(0f, 1f);
        oldOpacity.InsertKeyFrame(1f, 0f, easeIn);
        oldOpacity.Duration = outDuration;
        oldVisual.StartAnimation("Opacity", oldOpacity);

        var newOffset = c.CreateVector3KeyFrameAnimation();
        newOffset.InsertKeyFrame(0f, basePos - shift);
        newOffset.InsertKeyFrame(1f, basePos, easeOut);
        newOffset.Duration = inDuration;
        newOffset.DelayTime = inDelay;
        newOffset.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        liveVisual.StartAnimation("Offset", newOffset);
        var newOpacity = c.CreateScalarKeyFrameAnimation();
        newOpacity.InsertKeyFrame(0f, 0f);
        newOpacity.InsertKeyFrame(1f, 1f, easeOut);
        newOpacity.Duration = inDuration;
        newOpacity.DelayTime = inDelay;
        newOpacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        liveVisual.StartAnimation("Opacity", newOpacity);

        if (pop)
        {
            var oldScale = c.CreateVector3KeyFrameAnimation();
            oldScale.InsertKeyFrame(0f, Vector3.One);
            oldScale.InsertKeyFrame(1f, new Vector3(0.6f, 0.6f, 1), easeIn);
            oldScale.Duration = outDuration;
            oldVisual.StartAnimation("Scale", oldScale);
            var newScale = c.CreateVector3KeyFrameAnimation();
            newScale.InsertKeyFrame(0f, new Vector3(0.6f, 0.6f, 1));
            newScale.InsertKeyFrame(0.7f, new Vector3(1.08f, 1.08f, 1), easeOut);
            newScale.InsertKeyFrame(1f, Vector3.One);
            newScale.Duration = inDuration;
            newScale.DelayTime = inDelay;
            newScale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            liveVisual.StartAnimation("Scale", newScale);
        }

        batch.End();
        batch.Completed += (_, _) => Programa.NaInterface(() =>
        {
            try
            {
                if (!cena.Superficies.Remove(oldSurface)) return;   // the scene already released everything
                cena.Raiz.Children.Remove(oldVisual);
                oldVisual.Dispose();
                oldSurface.Dispose();
            }
            catch (Exception) { }
        });
    }

    /// <summary>The offset the scene gave the layer, remembered the first time (an animation leaves it at its end).</summary>
    private static Vector3 BaseOffset(SpriteVisual v)
    {
        if (v.Properties.TryGetVector3("swapBase", out var b) == CompositionGetValueStatus.Succeeded) return b;
        v.Properties.InsertVector3("swapBase", v.Offset);
        return v.Offset;
    }
}
