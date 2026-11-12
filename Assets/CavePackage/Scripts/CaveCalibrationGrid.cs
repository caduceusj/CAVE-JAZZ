using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CavePackage
{
    /// <summary>
    /// Grade de calibração da CAVE.
    ///
    /// • Grade de superfície: linhas EXATAMENTE sobre as paredes e o piso físicos (paralaxe zero).
    ///   Vale para qualquer ponto de vista: se a grade não cair reta nas quinas/bordas reais da sala,
    ///   o problema está no mapeamento do media server ou nas medidas da sala — não no ponto de vista.
    /// • Grade de profundidade (opcional): uma caixa N metros além das paredes. Do sweet spot as linhas
    ///   atravessam as quinas retas; fora dele elas "dobram" na quina. É o mesmo efeito do peixe que
    ///   curva na quina — útil para ver o tamanho do problema e escolher a altura/posição do olho.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("CAVE/Cave Calibration Grid")]
    public class CaveCalibrationGrid : MonoBehaviour
    {
        [Tooltip("Rig cuja sala será desenhada. Se vazio, procura no mesmo objeto ou nos pais.")]
        public CaveRig rig;

        [Tooltip("Espaçamento das linhas (m).")]
        [Min(0.05f)] public float spacing = 0.5f;

        [Tooltip("Espessura das linhas (m). ~1,5 cm fica visível sem ser grosseiro.")]
        [Min(0.001f)] public float lineWidth = 0.015f;

        [Tooltip("Linhas sobre as superfícies físicas.")]
        public bool surfaceGrid = true;

        [Tooltip("Caixa de linhas além das paredes (mostra a dobra nas quinas fora do sweet spot).")]
        public bool depthGrid = false;

        [Tooltip("Distância (m) da caixa de profundidade além das paredes e do piso.")]
        [Min(0.1f)] public float depthGridDistance = 2f;

        [Tooltip("Desenha por cima de tudo (ignora a cena). Desmarque para ver oclusão normal.")]
        public bool drawOnTop = true;

        [Tooltip("Mostrar só nas câmeras da CAVE (oculta na Scene view e nas outras câmeras).")]
        public bool onlyCaveCameras = false;

        [Tooltip("Material opcional (precisa usar cor de vértice). Vazio = material interno.")]
        public Material material;

        Mesh _mesh;
        Material _fallbackMaterial;
        int _builtHash;
        readonly List<Vector3> _vertices = new List<Vector3>();
        readonly List<Color> _colors = new List<Color>();
        readonly List<int> _indices = new List<int>();

        void OnEnable()
        {
            if (rig == null) rig = GetComponentInParent<CaveRig>();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            DestroySafe(_mesh);
            DestroySafe(_fallbackMaterial);
            _mesh = null;
            _fallbackMaterial = null;
            _builtHash = 0;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (rig == null || cam == null) return;
            if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
            if (onlyCaveCameras && !IsCaveCamera(cam)) return;

            EnsureMesh();
            var mat = material != null ? material : GetFallbackMaterial();
            if (_mesh == null || mat == null) return;

            var t = rig.transform;
            var matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one * rig.worldUnitsPerMeter);
            Graphics.DrawMesh(_mesh, matrix, mat, gameObject.layer, cam, 0, null, ShadowCastingMode.Off, false);
        }

        bool IsCaveCamera(Camera cam)
        {
            foreach (var s in rig.GetSurfaces())
                if (s != null && s.camera == cam) return true;
            return false;
        }

        Material GetFallbackMaterial()
        {
            if (_fallbackMaterial == null)
            {
                var shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) return null;
                _fallbackMaterial = new Material(shader) { name = "CAVE Calibration Grid", hideFlags = HideFlags.DontSave };
                _fallbackMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                _fallbackMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                _fallbackMaterial.SetFloat("_Cull", (float)CullMode.Off);
                _fallbackMaterial.SetFloat("_ZWrite", 0f);
                _fallbackMaterial.renderQueue = (int)RenderQueue.Overlay;
            }
            _fallbackMaterial.SetFloat("_ZTest", (float)(drawOnTop ? CompareFunction.Always : CompareFunction.LessEqual));
            return _fallbackMaterial;
        }

        // ------------------------------------------------------------------ malha
        int ComputeHash(Vector3 eye)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + rig.roomWidth.GetHashCode();
                h = h * 31 + rig.roomDepth.GetHashCode();
                h = h * 31 + rig.roomHeight.GetHashCode();
                h = h * 31 + spacing.GetHashCode();
                h = h * 31 + lineWidth.GetHashCode();
                h = h * 31 + surfaceGrid.GetHashCode();
                h = h * 31 + depthGrid.GetHashCode();
                h = h * 31 + depthGridDistance.GetHashCode();
                if (depthGrid) h = h * 31 + eye.GetHashCode();
                foreach (var s in rig.GetSurfaces()) h = h * 31 + (s != null && s.enabled ? 1 : 0);
                return h == 0 ? 1 : h;
            }
        }

        void EnsureMesh()
        {
            Vector3 eye = rig.GetEyeRoomPosition();
            int hash = ComputeHash(eye);
            if (_mesh != null && hash == _builtHash) return;
            _builtHash = hash;

            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();
            float w = rig.roomWidth, d = rig.roomDepth, h = rig.roomHeight;
            var surfaces = rig.GetSurfaces();

            if (surfaceGrid)
            {
                const float inset = 0.002f; // 2 mm para dentro da sala (fica na frente da superfície)
                foreach (var s in surfaces)
                {
                    if (s == null || !s.enabled) continue;
                    CaveGeometry.GetSurface(s.face, w, d, h, out var c, out var r, out var u, out var f, out var hw, out var hh);
                    AddPlaneGrid(c - f * inset, r, u, hw, hh, FaceColor(s.face), Color.white);
                }
            }

            if (depthGrid)
            {
                // Caixa fechada (sem teto) depthGridDistance além de cada parede e abaixo do piso.
                // A altura cobre o que se vê pela borda de cima das paredes a partir do olho.
                float g = depthGridDistance;
                float minWall = Mathf.Max(0.1f, Mathf.Min(w * 0.5f - Mathf.Abs(eye.x), d * 0.5f - Mathf.Abs(eye.z)));
                float top = Mathf.Max(h + g, eye.y + (h - eye.y) * (minWall + g) / minWall);
                float bw = w + 2f * g, bd = d + 2f * g, bh = top + g;
                var shift = new Vector3(0f, -g, 0f);
                foreach (var s in surfaces)
                {
                    if (s == null || !s.enabled) continue;
                    CaveGeometry.GetSurface(s.face, bw, bd, bh, out var c, out var r, out var u, out _, out var hw, out var hh);
                    var dim = FaceColor(s.face) * 0.6f;
                    dim.a = 0.9f;
                    AddPlaneGrid(c + shift, r, u, hw, hh, dim, dim);
                }
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "CAVE Calibration Grid", hideFlags = HideFlags.DontSave };
                _mesh.MarkDynamic();
            }
            _mesh.Clear();
            _mesh.indexFormat = _vertices.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_indices, 0);
            _mesh.RecalculateBounds();
        }

        void AddPlaneGrid(Vector3 center, Vector3 right, Vector3 up, float halfWidth, float halfHeight,
                          Color lineColor, Color borderColor)
        {
            float lw = lineWidth;
            // linhas ao longo de "up", simétricas a partir do centro (a central é mais grossa)
            int nx = Mathf.FloorToInt(halfWidth / spacing);
            for (int i = -nx; i <= nx; i++)
                AddStrip(center + right * (i * spacing), up, right, halfHeight, lw * (i == 0 ? 1f : 0.5f), lineColor);
            // linhas ao longo de "right"
            int ny = Mathf.FloorToInt(halfHeight / spacing);
            for (int j = -ny; j <= ny; j++)
                AddStrip(center + up * (j * spacing), right, up, halfWidth, lw * (j == 0 ? 1f : 0.5f), lineColor);
            // borda mais grossa, por dentro do limite exato da superfície
            float bw = lw * 1.5f;
            AddStrip(center - right * (halfWidth - bw), up, right, halfHeight, bw, borderColor);
            AddStrip(center + right * (halfWidth - bw), up, right, halfHeight, bw, borderColor);
            AddStrip(center - up * (halfHeight - bw), right, up, halfWidth, bw, borderColor);
            AddStrip(center + up * (halfHeight - bw), right, up, halfWidth, bw, borderColor);
        }

        // Faixa centrada em 'mid', comprida ao longo de 'along' (meio-comprimento 'halfLength'),
        // com meia-espessura 'halfThickness' ao longo de 'across'.
        void AddStrip(Vector3 mid, Vector3 along, Vector3 across, float halfLength, float halfThickness, Color color)
        {
            int start = _vertices.Count;
            _vertices.Add(mid - along * halfLength - across * halfThickness);
            _vertices.Add(mid + along * halfLength - across * halfThickness);
            _vertices.Add(mid + along * halfLength + across * halfThickness);
            _vertices.Add(mid - along * halfLength + across * halfThickness);
            for (int k = 0; k < 4; k++) _colors.Add(color);
            _indices.Add(start); _indices.Add(start + 1); _indices.Add(start + 2);
            _indices.Add(start); _indices.Add(start + 2); _indices.Add(start + 3);
        }

        static Color FaceColor(CaveFace face)
        {
            switch (face)
            {
                case CaveFace.Front: return new Color(0.2f, 0.6f, 1f, 1f);
                case CaveFace.Back: return new Color(1f, 0.55f, 0.1f, 1f);
                case CaveFace.Left: return new Color(0.3f, 1f, 0.3f, 1f);
                case CaveFace.Right: return new Color(1f, 0.3f, 0.8f, 1f);
                default: return new Color(1f, 1f, 0.3f, 1f);
            }
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }
    }
}
