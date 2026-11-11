using System;
using UnityEngine;

namespace CavePackage
{
    /// <summary>Superfícies físicas da CAVE.</summary>
    public enum CaveFace
    {
        Front,  // Parede frontal  (+Z)
        Back,   // Parede traseira (-Z)
        Left,   // Parede esquerda (-X)
        Right,  // Parede direita  (+X)
        Floor,  // Piso            (-Y)
    }

    /// <summary>Como o near plane da câmera de uma superfície é escolhido.</summary>
    public enum CaveNearMode
    {
        /// <summary>Near pequeno (CaveRig.nearClip): tudo entre o olho e a superfície é desenhado.</summary>
        Standard,

        /// <summary>
        /// Near plane colado na própria superfície (menos clipOffset): tudo que estiver entre o olho
        /// e a superfície é cortado. É o que o piso usa para não "pegar" mesa, peixe, água etc.
        /// que estejam entre o público e o chão.
        /// </summary>
        ClipAtSurface,
    }

    /// <summary>Configuração de uma superfície: câmera, região no atlas de saída e recorte.</summary>
    [Serializable]
    public class CaveSurface
    {
        [Tooltip("Qual superfície física esta entrada representa.")]
        public CaveFace face;

        [Tooltip("Câmera que renderiza esta superfície. Posição, rotação e projeção são controladas pelo CaveRig.")]
        public Camera camera;

        [Tooltip("Desmarque para não renderizar esta superfície.")]
        public bool enabled = true;

        [Tooltip("Canto inferior esquerdo da região desta superfície no atlas de saída, em pixels (origem embaixo à esquerda).")]
        public Vector2Int atlasPosition;

        [Tooltip("Tamanho da região no atlas, em pixels. A câmera renderiza exatamente nesse tamanho (1 pixel = 1 pixel, sem reamostragem).")]
        public Vector2Int atlasSize;

        [Tooltip("Standard: near pequeno. ClipAtSurface: corta tudo que estiver entre o olho e a superfície (usado no piso).")]
        public CaveNearMode nearMode = CaveNearMode.Standard;

        [Tooltip("Só para ClipAtSurface: distância (m) da superfície em direção ao olho onde o corte começa. " +
                 "No piso = altura acima do chão a partir da qual tudo é cortado (ex.: 0.05 = 5 cm).")]
        [Min(0f)]
        public float clipOffset = 0.05f;

        // ---- estado de runtime (não serializado)
        [NonSerialized] internal RenderTexture renderTexture;
        [NonSerialized] internal CaveCameraState savedState;
        [NonSerialized] internal bool applied;

        public CaveSurface() { }

        public CaveSurface(CaveFace face, Vector2Int atlasPosition, Vector2Int atlasSize,
                           CaveNearMode nearMode = CaveNearMode.Standard, float clipOffset = 0.05f)
        {
            this.face = face;
            this.atlasPosition = atlasPosition;
            this.atlasSize = atlasSize;
            this.nearMode = nearMode;
            this.clipOffset = clipOffset;
        }

        public RectInt AtlasRect => new RectInt(atlasPosition, atlasSize);
    }

    /// <summary>Estado original de uma câmera, para restaurar quando o rig é desligado.</summary>
    internal struct CaveCameraState
    {
        public bool valid;
        public RenderTexture targetTexture;
        public Vector3 localPosition;
        public Quaternion localRotation;
        public float nearClipPlane;
        public float farClipPlane;
        public float fieldOfView;
        public Rect rect;
        public bool usePhysicalProperties;

        public static CaveCameraState Capture(Camera cam)
        {
            var t = cam.transform;
            return new CaveCameraState
            {
                valid = true,
                targetTexture = cam.targetTexture,
                localPosition = t.localPosition,
                localRotation = t.localRotation,
                nearClipPlane = cam.nearClipPlane,
                farClipPlane = cam.farClipPlane,
                fieldOfView = cam.fieldOfView,
                rect = cam.rect,
                usePhysicalProperties = cam.usePhysicalProperties,
            };
        }

        public void Restore(Camera cam)
        {
            if (!valid || cam == null) return;
            cam.targetTexture = targetTexture;
            cam.transform.localPosition = localPosition;
            cam.transform.localRotation = localRotation;
            cam.nearClipPlane = nearClipPlane;
            cam.farClipPlane = farClipPlane;
            cam.fieldOfView = fieldOfView;
            cam.rect = rect;
            cam.usePhysicalProperties = usePhysicalProperties;
            cam.ResetAspect();
            cam.ResetProjectionMatrix();
            cam.ResetCullingMatrix();
        }
    }

    /// <summary>
    /// Geometria da sala. Coordenadas "da sala" em metros, origem no CENTRO DO PISO,
    /// +X = parede direita, +Y = para cima, +Z = parede frontal.
    /// </summary>
    public static class CaveGeometry
    {
        /// <summary>
        /// Eixos da imagem de cada superfície já na orientação do atlas (mapa UV da sala):
        /// forward = normal saindo da sala (para onde a câmera olha), up = "cima" da imagem no atlas.
        /// O "direita" da imagem é Cross(up, forward), como no Unity.
        /// Planificação em cruz: frontal em cima do piso, traseira embaixo (de cabeça para baixo),
        /// esquerda à esquerda (girada 90° anti-horário), direita à direita (girada 90° horário).
        /// </summary>
        public static void GetImageBasis(CaveFace face, out Vector3 forward, out Vector3 up)
        {
            switch (face)
            {
                case CaveFace.Front: forward = Vector3.forward; up = Vector3.up; break;
                case CaveFace.Back: forward = Vector3.back; up = Vector3.down; break;
                case CaveFace.Left: forward = Vector3.left; up = Vector3.forward; break;
                case CaveFace.Right: forward = Vector3.right; up = Vector3.forward; break;
                default: forward = Vector3.down; up = Vector3.forward; break; // Floor
            }
        }

        /// <summary>
        /// Retângulo físico da superfície (em metros, coordenadas da sala): centro, eixos da imagem
        /// e meias-dimensões ao longo do eixo "direita" (halfWidth) e "cima" (halfHeight) da imagem.
        /// </summary>
        public static void GetSurface(CaveFace face, float width, float depth, float height,
                                      out Vector3 center, out Vector3 right, out Vector3 up, out Vector3 forward,
                                      out float halfWidth, out float halfHeight)
        {
            GetImageBasis(face, out forward, out up);
            right = Vector3.Cross(up, forward);
            switch (face)
            {
                case CaveFace.Front:
                    center = new Vector3(0f, height * 0.5f, depth * 0.5f);
                    halfWidth = width * 0.5f; halfHeight = height * 0.5f; break;
                case CaveFace.Back:
                    center = new Vector3(0f, height * 0.5f, -depth * 0.5f);
                    halfWidth = width * 0.5f; halfHeight = height * 0.5f; break;
                case CaveFace.Left:
                    center = new Vector3(-width * 0.5f, height * 0.5f, 0f);
                    halfWidth = height * 0.5f; halfHeight = depth * 0.5f; break;
                case CaveFace.Right:
                    center = new Vector3(width * 0.5f, height * 0.5f, 0f);
                    halfWidth = height * 0.5f; halfHeight = depth * 0.5f; break;
                default: // Floor
                    center = Vector3.zero;
                    halfWidth = width * 0.5f; halfHeight = depth * 0.5f; break;
            }
        }

        /// <summary>
        /// Projeção off-axis (Kooima, "Generalized Perspective Projection"): o frustum passa
        /// exatamente pelas 4 bordas da superfície física vista do olho. É isso que faz a imagem
        /// das paredes e do piso "colar" nas quinas quando vista do sweet spot.
        /// Retorna false se o olho estiver atrás/no plano da superfície.
        /// </summary>
        public static bool ComputeOffAxis(CaveFace face, float width, float depth, float height, Vector3 eye,
                                          float near, bool clipAtSurface, float clipOffset, float minNear,
                                          out float left, out float rightExtent, out float bottom, out float top,
                                          out float nearUsed, out Quaternion rotation, out float surfaceDistance)
        {
            GetSurface(face, width, depth, height, out var c, out var r, out var u, out var f,
                       out var hw, out var hh);
            var v = c - eye;
            surfaceDistance = Vector3.Dot(v, f);
            rotation = Quaternion.LookRotation(f, u);
            if (surfaceDistance < 1e-4f)
            {
                left = rightExtent = bottom = top = nearUsed = 0f;
                return false;
            }

            float cx = Vector3.Dot(v, r);
            float cy = Vector3.Dot(v, u);
            nearUsed = clipAtSurface ? Mathf.Max(surfaceDistance - clipOffset, minNear) : Mathf.Max(near, minNear);
            float k = nearUsed / surfaceDistance;
            left = (cx - hw) * k;
            rightExtent = (cx + hw) * k;
            bottom = (cy - hh) * k;
            top = (cy + hh) * k;
            return true;
        }

        /// <summary>Matriz de projeção estilo OpenGL (glFrustum), a convenção de Camera.projectionMatrix.</summary>
        public static Matrix4x4 Frustum(float l, float r, float b, float t, float n, float f)
        {
            var m = new Matrix4x4();
            m.m00 = 2f * n / (r - l); m.m01 = 0f; m.m02 = (r + l) / (r - l); m.m03 = 0f;
            m.m10 = 0f; m.m11 = 2f * n / (t - b); m.m12 = (t + b) / (t - b); m.m13 = 0f;
            m.m20 = 0f; m.m21 = 0f; m.m22 = -(f + n) / (f - n); m.m23 = -2f * f * n / (f - n);
            m.m30 = 0f; m.m31 = 0f; m.m32 = -1f; m.m33 = 0f;
            return m;
        }
    }
}
