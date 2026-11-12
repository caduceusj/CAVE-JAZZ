using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace CavePackage
{
    /// <summary>
    /// Rig de projeção da CAVE (4 paredes + piso).
    ///
    /// Coloque este componente no objeto que representa o CENTRO DO PISO da sala física dentro do
    /// mundo virtual (o CaveRoot). O eixo +Z do objeto aponta para a parede frontal.
    ///
    /// O que ele faz a cada frame:
    ///  1. Todas as câmeras ficam no mesmo ponto de vista (sweet spot, ex.: olhos a 1,60 m).
    ///  2. Cada câmera recebe uma projeção off-axis cujo frustum passa exatamente pelas bordas da
    ///     sua superfície física (medidas reais da sala) -> as imagens se encontram nas quinas.
    ///  3. O piso usa near plane colado no chão: tudo que estiver entre o público e o piso é cortado,
    ///     sem precisar aproximar a câmera do chão nem abrir a lente (fim da distorção "fisheye").
    ///  4. Cada câmera renderiza numa textura do tamanho exato da sua região no atlas e a região é
    ///     copiada pixel a pixel (Graphics.CopyTexture) para a textura enviada pelo Spout. Não há mais
    ///     canvas, reamostragem bilinear, meio pixel de deslocamento nem pós-processamento duplicado.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)] // depois de scripts que movem o CaveRoot ou a cabeça rastreada
    [AddComponentMenu("CAVE/Cave Rig")]
    public class CaveRig : MonoBehaviour
    {
        const float MinNear = 0.01f;

        // ------------------------------------------------------------------ sala física
        [Header("Sala física (metros) — MEÇA A SALA REAL")]
        [Tooltip("Distância entre a parede esquerda e a direita (eixo X), em metros.")]
        [Min(0.1f)] public float roomWidth = 4.50f;

        [Tooltip("Distância entre a parede traseira e a frontal (eixo Z), em metros.")]
        [Min(0.1f)] public float roomDepth = 8.40f;

        [Tooltip("Altura da área projetada nas paredes (do piso até a borda de cima da imagem), em metros.")]
        [Min(0.1f)] public float roomHeight = 3.00f;

        // ------------------------------------------------------------------ ponto de vista
        [Header("Ponto de vista (sweet spot)")]
        [Tooltip("Posição dos olhos do público em metros, relativa ao CENTRO DO PISO. " +
                 "Y = altura dos olhos (≈1,60 m em pé, ≈1,20 m sentado/crianças).")]
        public Vector3 eyePosition = new Vector3(0f, 1.6f, 0f);

        [Tooltip("Opcional: transform rastreado (cabeça de um visitante). Se definido, substitui o eyePosition.")]
        public Transform trackedEye;

        // ------------------------------------------------------------------ escala / recorte
        [Header("Escala e recorte")]
        [Tooltip("Unidades do mundo virtual por metro físico. 1 = escala real (1 unidade = 1 m).")]
        [Min(0.001f)] public float worldUnitsPerMeter = 1f;

        [Tooltip("Near plane (metros) das superfícies em modo Standard.")]
        [Min(MinNear)] public float nearClip = 0.05f;

        [Tooltip("Far plane, em unidades do mundo.")]
        [Min(1f)] public float farClip = 1000f;

        // ------------------------------------------------------------------ superfícies
        [Header("Superfícies (câmera + região no atlas)")]
        public CaveSurface front = new CaveSurface(CaveFace.Front, new Vector2Int(934, 3559), new Vector2Int(1406, 941));
        public CaveSurface back = new CaveSurface(CaveFace.Back, new Vector2Int(934, 0), new Vector2Int(1406, 933));
        public CaveSurface left = new CaveSurface(CaveFace.Left, new Vector2Int(0, 933), new Vector2Int(934, 2626));
        public CaveSurface right = new CaveSurface(CaveFace.Right, new Vector2Int(2340, 933), new Vector2Int(930, 2626));
        public CaveSurface floor = new CaveSurface(CaveFace.Floor, new Vector2Int(934, 933), new Vector2Int(1406, 2626),
                                                   CaveNearMode.ClipAtSurface, 0.05f);

        // ------------------------------------------------------------------ saída
        [Header("Saída (atlas lido pelo SpoutSender)")]
        [Tooltip("Textura final enviada pelo Spout (ex.: RT_Send). Se vazio, o rig cria uma em runtime (propriedade Output).")]
        public RenderTexture outputTexture;

        [Tooltip("Tamanho do atlas quando o próprio rig cria a textura de saída (deve bater com o mapa UV da sala).")]
        public Vector2Int outputSize = new Vector2Int(3270, 4500);

        [Tooltip("Câmera do compositor antigo (CamSender). É desligada enquanto o rig estiver ativo e religada depois.")]
        public Camera legacyCompositorCamera;

        [Tooltip("Canvas do compositor antigo (CanvasAligned). É desativado enquanto o rig estiver ativo e reativado depois.")]
        public GameObject legacyCanvas;

        // ------------------------------------------------------------------ editor
        [Header("Editor")]
        [Tooltip("Aplica o rig também fora do Play Mode (para calibrar). O estado original das câmeras é " +
                 "restaurado antes de salvar a cena e antes de entrar no Play Mode.")]
        public bool previewInEditMode = false;

        [Tooltip("Desenha a sala, o olho e os frustums na Scene view.")]
        public bool drawGizmos = true;

        // ------------------------------------------------------------------ runtime
        bool _active;
        bool _pendingRebuild;
        bool _quitting;
        RenderTexture _output;
        bool _ownsOutput;
        bool _legacyCameraWasEnabled;
        bool _legacyCanvasWasActive;
        readonly CaveSurface[] _surfaceBuffer = new CaveSurface[5];
        readonly HashSet<Camera> _surfaceCameras = new HashSet<Camera>();
        readonly List<string> _warnings = new List<string>();

        /// <summary>Textura final (atlas) que está sendo composta.</summary>
        public RenderTexture Output => _active ? _output : outputTexture;

        /// <summary>True enquanto o rig controla as câmeras.</summary>
        public bool IsActive => _active;

        /// <summary>As 5 superfícies (frontal, traseira, esquerda, direita, piso).</summary>
        public CaveSurface[] GetSurfaces() => new[] { front, back, left, right, floor };

        CaveSurface[] SurfaceArray()
        {
            _surfaceBuffer[0] = front;
            _surfaceBuffer[1] = back;
            _surfaceBuffer[2] = left;
            _surfaceBuffer[3] = right;
            _surfaceBuffer[4] = floor;
            return _surfaceBuffer;
        }

        // ================================================================== ciclo de vida
        void OnEnable()
        {
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            Application.quitting += OnQuitting;
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            Application.quitting -= OnQuitting;
#if UNITY_EDITOR
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
            Deactivate();
        }

        void OnQuitting() => _quitting = true;

        // Ativação/desativação só acontecem aqui (fora de callbacks de render e do OnValidate).
        void LateUpdate()
        {
            if (_pendingRebuild)
            {
                _pendingRebuild = false;
                Deactivate();
            }

            bool run = ShouldRun;
            if (run && !_active) Activate();
            else if (!run && _active) Deactivate();

            if (_active) UpdateCameras();
        }

        // Reaplica as matrizes logo antes de renderizar (pega movimentos feitos depois do LateUpdate).
        void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (_active) UpdateCameras();
        }

        void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!_active || cameras == null) return;
            // Só compõe depois do contexto que renderizou as câmeras da CAVE (ignora Scene view, previews...).
            for (int i = 0; i < cameras.Count; i++)
            {
                if (_surfaceCameras.Contains(cameras[i]))
                {
                    Composite();
                    return;
                }
            }
        }

        bool ShouldRun
        {
            get
            {
                if (!isActiveAndEnabled) return false;
                if (Application.isPlaying) return true;
#if UNITY_EDITOR
                if (EditorApplication.isPlayingOrWillChangePlaymode) return false;
                if (gameObject.scene.IsValid() && EditorSceneManager.IsPreviewScene(gameObject.scene)) return false; // Prefab Mode
#endif
                return previewInEditMode;
            }
        }

        // ================================================================== ativar / desativar
        void Activate()
        {
            _warnings.Clear();

            // ---- textura de saída (atlas)
            if (outputTexture != null)
            {
                _output = outputTexture;
                _ownsOutput = false;
            }
            else
            {
                var desc = new RenderTextureDescriptor(Mathf.Max(1, outputSize.x), Mathf.Max(1, outputSize.y),
                                                       RenderTextureFormat.ARGB32, 0);
                desc.sRGB = true;
                _output = new RenderTexture(desc) { name = "CAVE_Output", hideFlags = HideFlags.DontSave };
                _ownsOutput = true;
            }
            if (!_output.IsCreated()) _output.Create();
            if (!_output.sRGB && QualitySettings.activeColorSpace == ColorSpace.Linear)
                _warnings.Add($"'{_output.name}' não é sRGB: com 8 bits lineares as sombras ficam em degraus (banding). " +
                              "Use Color Format R8G8B8A8_SRGB.");
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
                _warnings.Add("Esta plataforma não suporta Graphics.CopyTexture; o atlas não será composto.");

            ClearOutput();

            // ---- câmeras das superfícies
            _surfaceCameras.Clear();
            foreach (var s in SurfaceArray())
            {
                if (s == null) continue;
                s.applied = false;
                if (!s.enabled) continue;
                if (s.camera == null) { _warnings.Add($"{s.face}: nenhuma câmera atribuída."); continue; }
                if (_surfaceCameras.Contains(s.camera)) { _warnings.Add($"{s.face}: a câmera '{s.camera.name}' já é usada por outra superfície."); continue; }
                if (!RectFits(s.AtlasRect)) { _warnings.Add($"{s.face}: região {s.AtlasRect} fora do atlas {_output.width}x{_output.height}."); continue; }

                s.savedState = CaveCameraState.Capture(s.camera);
                s.renderTexture = CreateSurfaceTexture(s);
                s.camera.targetTexture = s.renderTexture;
                s.camera.rect = new Rect(0f, 0f, 1f, 1f);
                s.camera.usePhysicalProperties = false;
                s.applied = true;
                _surfaceCameras.Add(s.camera);
            }
            CheckOverlaps();

            // ---- compositor antigo (canvas + CamSender) fica desligado enquanto o rig roda
            if (legacyCompositorCamera != null)
            {
                _legacyCameraWasEnabled = legacyCompositorCamera.enabled;
                legacyCompositorCamera.enabled = false;
            }
            if (legacyCanvas != null)
            {
                _legacyCanvasWasActive = legacyCanvas.activeSelf;
                legacyCanvas.SetActive(false);
            }

            foreach (var w in _warnings) Debug.LogWarning($"[CaveRig] {w}", this);
            _active = true;
            UpdateCameras();
        }

        void Deactivate()
        {
            if (!_active) return;
            _active = false;

            foreach (var s in SurfaceArray())
            {
                if (s == null) continue;
                if (s.applied && s.camera != null) s.savedState.Restore(s.camera);
                s.applied = false;
                s.savedState = default;
                if (s.renderTexture != null)
                {
                    s.renderTexture.Release();
                    DestroySafe(s.renderTexture);
                    s.renderTexture = null;
                }
            }
            _surfaceCameras.Clear();

            if (!_quitting)
            {
                if (legacyCompositorCamera != null) legacyCompositorCamera.enabled = _legacyCameraWasEnabled;
                if (legacyCanvas != null && legacyCanvas.scene.isLoaded) legacyCanvas.SetActive(_legacyCanvasWasActive);
            }

            if (_ownsOutput && _output != null)
            {
                _output.Release();
                DestroySafe(_output);
            }
            _output = null;
            _ownsOutput = false;
        }

        RenderTexture CreateSurfaceTexture(CaveSurface s)
        {
            // Mesmo formato de cor do atlas (requisito do CopyTexture), tamanho exato da região.
            var desc = _output.descriptor;
            desc.width = s.atlasSize.x;
            desc.height = s.atlasSize.y;
            desc.depthBufferBits = 24;
            desc.msaaSamples = 1;
            desc.useMipMap = false;
            desc.autoGenerateMips = false;
            desc.enableRandomWrite = false;
            desc.dimension = TextureDimension.Tex2D;
            desc.volumeDepth = 1;
            var rt = new RenderTexture(desc)
            {
                name = "CAVE_" + s.face,
                hideFlags = HideFlags.DontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            return rt;
        }

        void ClearOutput()
        {
            if (_output == null) return;
            var previous = RenderTexture.active;
            RenderTexture.active = _output;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;
        }

        void Composite()
        {
            if (_output == null || SystemInfo.copyTextureSupport == CopyTextureSupport.None) return;
            if (!_output.IsCreated())
            {
                _output.Create();
                ClearOutput();
            }
            foreach (var s in SurfaceArray())
            {
                if (s == null || !s.applied || s.renderTexture == null) continue;
                if (!s.renderTexture.IsCreated()) continue;
                Graphics.CopyTexture(s.renderTexture, 0, 0, 0, 0, s.atlasSize.x, s.atlasSize.y,
                                     _output, 0, 0, s.atlasPosition.x, s.atlasPosition.y);
            }
        }

        // ================================================================== câmeras
        /// <summary>Posição do olho em coordenadas da sala (metros, origem no centro do piso).</summary>
        public Vector3 GetEyeRoomPosition()
        {
            Vector3 eye = eyePosition;
            if (trackedEye != null)
            {
                float s = Mathf.Max(worldUnitsPerMeter, 1e-6f);
                eye = Quaternion.Inverse(transform.rotation) * (trackedEye.position - transform.position) / s;
            }
            // O olho precisa ficar dentro da sala para todas as superfícies terem frustum válido.
            const float margin = 0.01f;
            eye.x = Mathf.Clamp(eye.x, -roomWidth * 0.5f + margin, roomWidth * 0.5f - margin);
            eye.z = Mathf.Clamp(eye.z, -roomDepth * 0.5f + margin, roomDepth * 0.5f - margin);
            eye.y = Mathf.Max(eye.y, margin);
            return eye;
        }

        /// <summary>Posição do olho no mundo virtual.</summary>
        public Vector3 EyeWorldPosition => transform.position + transform.rotation * (GetEyeRoomPosition() * worldUnitsPerMeter);

        void UpdateCameras()
        {
            Vector3 eye = GetEyeRoomPosition();
            float scale = worldUnitsPerMeter;
            Quaternion rootRotation = transform.rotation;
            Vector3 eyeWorld = transform.position + rootRotation * (eye * scale);

            foreach (var s in SurfaceArray())
            {
                if (s == null || !s.applied || s.camera == null) continue;

                bool clip = s.nearMode == CaveNearMode.ClipAtSurface;
                if (!CaveGeometry.ComputeOffAxis(s.face, roomWidth, roomDepth, roomHeight, eye,
                                                nearClip, clip, s.clipOffset, MinNear,
                                                out var l, out var r, out var b, out var t,
                                                out var n, out var localRotation, out _))
                    continue;

                // metros -> unidades do mundo
                l *= scale; r *= scale; b *= scale; t *= scale;
                float nearW = n * scale;
                float farW = Mathf.Max(farClip, nearW * 2f + 0.01f);

                var cam = s.camera;
                cam.transform.SetPositionAndRotation(eyeWorld, rootRotation * localRotation);
                cam.nearClipPlane = nearW;
                cam.farClipPlane = farW;
                cam.fieldOfView = Mathf.Clamp((Mathf.Atan(t / nearW) - Mathf.Atan(b / nearW)) * Mathf.Rad2Deg, 1f, 179f);
                cam.aspect = (r - l) / (t - b);
                cam.projectionMatrix = CaveGeometry.Frustum(l, r, b, t, nearW, farW); // sempre por último
            }
        }

        // ================================================================== layout / validação
        bool RectFits(RectInt rect)
        {
            return rect.width > 0 && rect.height > 0 && rect.xMin >= 0 && rect.yMin >= 0 &&
                   _output != null && rect.xMax <= _output.width && rect.yMax <= _output.height;
        }

        void CheckOverlaps()
        {
            var list = new List<CaveSurface>();
            foreach (var s in SurfaceArray()) if (s != null && s.applied) list.Add(s);
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (list[i].AtlasRect.Overlaps(list[j].AtlasRect))
                        _warnings.Add($"As regiões de {list[i].face} e {list[j].face} se sobrepõem no atlas.");
        }

        /// <summary>Layout do arquivo "UV Map - Sala Imersiva" (3270x4500). Padrão.</summary>
        [ContextMenu("Layout/Mapa UV da sala (3270x4500) - padrão")]
        public void ApplyUvMapLayout()
        {
            outputSize = new Vector2Int(3270, 4500);
            SetRect(front, 934, 3559, 1406, 941);
            SetRect(back, 934, 0, 1406, 933);
            SetRect(left, 0, 933, 934, 2626);
            SetRect(right, 2340, 933, 930, 2626);
            SetRect(floor, 934, 933, 1406, 2626);
            RequestRebuild();
        }

        /// <summary>Layout equivalente ao que o CanvasAligned antigo mostrava (sem as sobreposições).</summary>
        [ContextMenu("Layout/Igual ao CanvasAligned antigo")]
        public void ApplyLegacyCanvasLayout()
        {
            outputSize = new Vector2Int(3270, 4500);
            SetRect(front, 934, 3563, 1402, 937);
            SetRect(back, 934, 0, 1402, 938);
            SetRect(left, 0, 938, 934, 2625);
            SetRect(right, 2336, 938, 934, 2625);
            SetRect(floor, 934, 938, 1402, 2625);
            RequestRebuild();
        }

        /// <summary>
        /// Calcula largura e profundidade a partir da ALTURA medida e das proporções em pixels do atlas
        /// (assume densidade de pixels uniforme no mapa UV). Use só se não der para medir tudo.
        /// </summary>
        [ContextMenu("Medidas/Estimar largura e profundidade pela altura + mapa UV")]
        public void EstimateRoomFromAtlas()
        {
            float wallPx = (front.atlasSize.y + back.atlasSize.y + left.atlasSize.x + right.atlasSize.x) * 0.25f;
            if (wallPx <= 0f) return;
            float metersPerPixel = roomHeight / wallPx;
            roomWidth = floor.atlasSize.x * metersPerPixel;
            roomDepth = floor.atlasSize.y * metersPerPixel;
            RequestRebuild();
        }

        static void SetRect(CaveSurface s, int x, int y, int w, int h)
        {
            if (s == null) return;
            s.atlasPosition = new Vector2Int(x, y);
            s.atlasSize = new Vector2Int(w, h);
        }

        void RequestRebuild()
        {
            if (_active) _pendingRebuild = true;
#if UNITY_EDITOR
            EditorUtility.SetDirty(this);
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        void OnValidate()
        {
            roomWidth = Mathf.Max(0.1f, roomWidth);
            roomDepth = Mathf.Max(0.1f, roomDepth);
            roomHeight = Mathf.Max(0.1f, roomHeight);
            worldUnitsPerMeter = Mathf.Max(0.001f, worldUnitsPerMeter);
            nearClip = Mathf.Max(MinNear, nearClip);
            if (front != null) front.face = CaveFace.Front;
            if (back != null) back.face = CaveFace.Back;
            if (left != null) left.face = CaveFace.Left;
            if (right != null) right.face = CaveFace.Right;
            if (floor != null) floor.face = CaveFace.Floor;

            // Mudou região/câmera/saída com o rig ativo: recria as texturas no próximo LateUpdate.
            if (_active && NeedsRebuild()) _pendingRebuild = true;
#if UNITY_EDITOR
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        bool NeedsRebuild()
        {
            if (outputTexture != null && _output != outputTexture) return true;
            foreach (var s in SurfaceArray())
            {
                if (s == null) continue;
                if (s.applied != (s.enabled && s.camera != null)) return true;
                if (!s.applied) continue;
                var rt = s.renderTexture;
                if (rt == null || rt.width != s.atlasSize.x || rt.height != s.atlasSize.y) return true;
                if (s.camera.targetTexture != rt) return true;
            }
            return false;
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

#if UNITY_EDITOR
        // Antes de salvar a cena: devolve as câmeras ao estado original para não gravar
        // referências a texturas de runtime nem projeções modificadas.
        void OnSceneSaving(UnityEngine.SceneManagement.Scene scene, string path)
        {
            if (_active && scene == gameObject.scene) Deactivate();
        }

        void OnSceneSaved(UnityEngine.SceneManagement.Scene scene)
        {
            if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate(); // reativa o preview
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode) Deactivate();
        }
#endif

        // ================================================================== gizmos
        static Color FaceColor(CaveFace face)
        {
            switch (face)
            {
                case CaveFace.Front: return new Color(0.2f, 0.6f, 1f);
                case CaveFace.Back: return new Color(1f, 0.55f, 0.1f);
                case CaveFace.Left: return new Color(0.3f, 1f, 0.3f);
                case CaveFace.Right: return new Color(1f, 0.3f, 0.8f);
                default: return new Color(1f, 1f, 0.3f);
            }
        }

        void OnDrawGizmos()
        {
            if (!drawGizmos) return;
            var previousMatrix = Gizmos.matrix;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one * worldUnitsPerMeter);

            // sala
            Gizmos.color = new Color(1f, 1f, 1f, 0.75f);
            Gizmos.DrawWireCube(new Vector3(0f, roomHeight * 0.5f, 0f), new Vector3(roomWidth, roomHeight, roomDepth));

            // olho
            Vector3 eye = GetEyeRoomPosition();
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(eye, 0.05f);

            // superfícies e frustums
            foreach (var s in SurfaceArray())
            {
                if (s == null || !s.enabled) continue;
                CaveGeometry.GetSurface(s.face, roomWidth, roomDepth, roomHeight,
                                        out var c, out var r, out var u, out _, out var hw, out var hh);
                var p00 = c - r * hw - u * hh;
                var p10 = c + r * hw - u * hh;
                var p11 = c + r * hw + u * hh;
                var p01 = c - r * hw + u * hh;
                var color = FaceColor(s.face);
                Gizmos.color = color;
                Gizmos.DrawLine(p00, p10); Gizmos.DrawLine(p10, p11); Gizmos.DrawLine(p11, p01); Gizmos.DrawLine(p01, p00);
                Gizmos.DrawLine(c, c + u * Mathf.Min(hh, 0.5f)); // seta = "cima" da imagem no atlas
                color.a = 0.35f;
                Gizmos.color = color;
                Gizmos.DrawLine(eye, p00); Gizmos.DrawLine(eye, p10); Gizmos.DrawLine(eye, p11); Gizmos.DrawLine(eye, p01);
            }

            // plano de corte do piso
            if (floor != null && floor.enabled && floor.nearMode == CaveNearMode.ClipAtSurface)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.8f);
                Gizmos.DrawWireCube(new Vector3(0f, floor.clipOffset, 0f), new Vector3(roomWidth, 0f, roomDepth));
            }

            Gizmos.matrix = previousMatrix;

#if UNITY_EDITOR
            var style = new GUIStyle(EditorStyles.boldLabel);
            foreach (var s in SurfaceArray())
            {
                if (s == null || !s.enabled) continue;
                CaveGeometry.GetSurface(s.face, roomWidth, roomDepth, roomHeight, out var c, out _, out _, out _, out _, out _);
                style.normal.textColor = FaceColor(s.face);
                Handles.Label(transform.position + transform.rotation * (c * worldUnitsPerMeter), s.face.ToString(), style);
            }
#endif
        }
    }
}
