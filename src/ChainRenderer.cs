using System;
using System.Collections.Generic;
using EHS;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChainedIsHard
{
    /// <summary>
    /// Draws the chain between every pair of players, on every machine.
    ///
    /// Purely cosmetic and purely local - there is nothing to replicate, since each client
    /// already knows the whole chain and where everyone is. What it does have to sell is the
    /// state of a link: a rope drawn as a taut straight line looks the same whether you have
    /// five metres of slack or none, and the whole feel of the mod is knowing when it is about
    /// to bite. So the line droops by whatever slack is left and pulls straight as it runs out.
    ///
    /// One LineRenderer per link, reused between frames and only rebuilt when the chain itself
    /// changes.
    /// </summary>
    internal sealed class ChainRenderer
    {
        /// <summary>Points per link. Enough for the droop to read as a curve, few enough to be free.</summary>
        private const int Segments = 14;

        private readonly List<LineRenderer> lines = new();
        private readonly Vector3[] points = new Vector3[Segments];

        private GameObject host;
        private Material material;
        private bool unsupported;

        public void Tick(ChainedSettings settings, ChainTopology topology)
        {
            if (unsupported)
            {
                return;
            }

            if (!settings.ShowChain.Value || !settings.Value(settings.Enabled) || !topology.Ready)
            {
                Hide();
                return;
            }

            try
            {
                Draw(settings, topology);
            }
            catch (Exception ex)
            {
                unsupported = true;
                Clear();
                ChainedPlugin.Logger.LogWarning($"Chain drawing turned off, it failed: {ex.Message}");
            }
        }

        public void Clear()
        {
            lines.Clear();

            if (host != null)
            {
                Object.Destroy(host);
                host = null;
            }

            material = null;
        }

        private void Draw(ChainedSettings settings, ChainTopology topology)
        {
            IReadOnlyList<int> order = topology.Order;
            float length = Mathf.Max(0.5f, settings.Value(settings.ChainLength));
            Color color = settings.ResolvedChainColor;
            float width = settings.ChainWidth.Value;

            int drawn = 0;

            for (int i = 0; i < order.Count - 1; i++)
            {
                if (!TryEndpoints(order[i], order[i + 1], out Vector3 from, out Vector3 to))
                {
                    continue;
                }

                LineRenderer line = LineAt(drawn);

                if (line == null)
                {
                    return;
                }

                BuildCurve(from, to, length, settings.ChainSag.Value);

                line.positionCount = points.Length;
                line.SetPositions(points);
                line.widthMultiplier = width;
                line.startColor = color;
                line.endColor = color;
                line.enabled = true;

                drawn++;
            }

            // Links that could not be drawn this frame - someone respawning, someone not
            // spawned yet - leave their renderer behind, so it is switched off rather than
            // left showing a chain to a player who is not there.
            for (int i = drawn; i < lines.Count; i++)
            {
                lines[i].enabled = false;
            }
        }

        private static bool TryEndpoints(int fromId, int toId, out Vector3 from, out Vector3 to)
        {
            from = Vector3.zero;
            to = Vector3.zero;

            PlayerRef a = ChainTopology.Find(fromId);
            PlayerRef b = ChainTopology.Find(toId);

            return ChainTopology.TryGetPosition(a, out from) && ChainTopology.TryGetPosition(b, out to);
        }

        /// <summary>
        /// A rope hanging between two points, sagging by whatever length is not being used up
        /// by the distance. Not a real catenary - a parabola through the same three points,
        /// which costs a multiply and is indistinguishable at this size.
        /// </summary>
        private void BuildCurve(Vector3 from, Vector3 to, float length, float sagFactor)
        {
            float distance = Vector3.Distance(from, to);
            float sag = Mathf.Max(0f, length - distance) * sagFactor;

            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (float)(points.Length - 1);
                Vector3 point = Vector3.Lerp(from, to, t);

                // 4t(1-t) peaks at 1 in the middle and is 0 at both ends, so the rope stays
                // attached to the players however much it droops.
                point.y -= sag * (4f * t * (1f - t));

                points[i] = point;
            }
        }

        private LineRenderer LineAt(int index)
        {
            while (lines.Count <= index)
            {
                LineRenderer created = Create();

                if (created == null)
                {
                    return null;
                }

                lines.Add(created);
            }

            // Destroyed under us - a scene load takes the host object with it if something
            // else got hold of it - so it is rebuilt rather than written to.
            if (lines[index] == null)
            {
                LineRenderer replacement = Create();

                if (replacement == null)
                {
                    return null;
                }

                lines[index] = replacement;
            }

            return lines[index];
        }

        private LineRenderer Create()
        {
            if (host == null)
            {
                host = new GameObject("ChainedIsHardChains");
                Object.DontDestroyOnLoad(host);
                host.hideFlags = HideFlags.HideAndDontSave;
            }

            if (material == null)
            {
                // Nothing here can rely on a specific shader existing: the game is on URP,
                // where the old built-in line shaders may or may not be in the build.
                Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");

                if (shader == null)
                {
                    unsupported = true;
                    Clear();
                    ChainedPlugin.Logger.LogWarning(
                        "No usable shader for the chain in this build, so it stays invisible. " +
                        "The pull still works.");
                    return null;
                }

                material = new Material(shader);
            }

            var carrier = new GameObject($"Link{lines.Count}");
            carrier.transform.SetParent(host.transform, false);

            var line = carrier.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.loop = false;
            line.numCapVertices = 0;
            line.numCornerVertices = 0;
            line.receiveShadows = false;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.alignment = LineAlignment.View;
            line.material = material;

            return line;
        }

        private void Hide()
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != null)
                {
                    lines[i].enabled = false;
                }
            }
        }
    }
}
