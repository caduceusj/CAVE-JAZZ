using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CaveJazz.Calibration
{
    /// <summary>
    /// Desenha a CAVE por cima do CaveRoot: a caixa da sala, o ponto de olho, o frustum
    /// real de cada camera e, opcionalmente, as cinco faces com as proprias Render Textures
    /// aplicadas, na proporcao de cada uma.
    ///
    /// Os quads sao gerados como filhos <see cref="HideFlags.DontSave"/>: nunca entram na
    /// cena salva nem viram override do prefab, e somem quando o toggle e desligado.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CaveRig))]
    [AddComponentMenu("CAVE/Cave Preview")]
    public class CavePreview : MonoBehaviour
    {
        private const string ContainerName = "__CavePreview";

        [Header("Gizmos")]
        public bool showWireBox = true;
        public bool showFaceOutlines = true;
        public bool showEyePoint = true;

        [Tooltip("Liga o ponto de olho aos quatro cantos que cada camera realmente alcanca na parede.")]
        public bool showFrustums = true;

        [Tooltip("Pinta de vermelho a area realmente coberta quando ela foge da tolerancia do rig.")]
        public bool highlightMismatch = true;

        [Tooltip("Desenha os gizmos mesmo com o CaveRoot fora da selecao.")]
        public bool alwaysDrawGizmos = true;

        [Header("Cores")]
        public Color boxColor = new Color(0.25f, 0.85f, 1f, 1f);
        public Color faceColor = new Color(0.25f, 0.85f, 1f, 0.6f);
        public Color eyeColor = new Color(1f, 0.85f, 0.2f, 1f);
        public Color coverageOkColor = new Color(0.3f, 1f, 0.45f, 1f);
        public Color coverageErrorColor = new Color(1f, 0.3f, 0.3f, 1f);

        [Header("Faces com Render Texture")]
        [Tooltip("Gera as cinco faces como geometria, exibindo a Render Texture de cada camera.")]
        public bool showFaceQuads;

        [Range(0f, 1f)]
        public float quadOpacity = 1f;

        [Tooltip("Afasta os quads da parede na direcao do olho, para nao brigarem com a geometria da cena.")]
        public float quadOffset = 0.02f;

        [Tooltip("Esconde os quads das proprias cameras do rig, para o preview nao aparecer dentro do render da CAVE.")]
        public bool hideQuadsFromRigCameras = true;

        [Tooltip("Mantem os objetos do preview fora da Hierarchy.")]
        public bool hideQuadsInHierarchy;

        private CaveRig cachedRig;
        private Transform container;
        private readonly List<MeshRenderer> quadRenderers = new List<MeshRenderer>();
        private readonly List<MeshFilter> quadFilters = new List<MeshFilter>();
        private readonly List<CaveFace> quadFaces = new List<CaveFace>();
        private readonly List<Object> generatedAssets = new List<Object>();
        private readonly Vector3[] cornerBuffer = new Vector3[4];
        private bool quadsDirty = true;
        private float appliedQuadOpacity = -1f;
        private readonly int[] cachedRenderTextureIds = new int[CaveRig.FaceCount];
        private readonly Vector2Int[] cachedRenderTextureSizes = new Vector2Int[CaveRig.FaceCount];

        public CaveRig Rig
        {
            get
            {
                if (cachedRig == null)
                {
                    cachedRig = GetComponent<CaveRig>();
                }

                return cachedRig;
            }
        }

        // ------------------------------------------------------------------ ciclo de vida

        private void OnEnable()
        {
            quadsDirty = true;
            RenderPipelineManager.beginCameraRendering += HandleBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= HandleBeginCameraRendering;

            // Nao destroi aqui: destruir GameObjects dentro de OnDisable e fonte de aviso
            // no editor. Desativar basta, e o Update reconstroi quando voltar.
            if (container != null)
            {
                container.gameObject.SetActive(false);
            }
        }

        private void OnValidate()
        {
            quadsDirty = true;
        }

        private void OnDestroy()
        {
            DestroyContainerDeferred();
        }

        private void Update()
        {
            if (!showFaceQuads)
            {
                if (container != null)
                {
                    DestroyContainerImmediateSafe();
                }

                return;
            }

            if (container != null && !container.gameObject.activeSelf)
            {
                container.gameObject.SetActive(true);
            }

            // Avaliado sempre, e nao dentro de um ||: o cache precisa acompanhar mesmo
            // quando ja vamos reconstruir por outro motivo.
            bool texturesChanged = RenderTexturesChanged();

            if (quadsDirty || texturesChanged)
            {
                RebuildQuads();
                return;
            }

            // Mover uma camera nao dispara OnValidate. Como calibrar e exatamente mexer
            // nas cameras, os vertices sao reavaliados todo frame: sao 5 quads de 4 pontos.
            RefreshQuadGeometry();
        }

        // ------------------------------------------------------------------ quads

        private bool RenderTexturesChanged()
        {
            CaveRig rig = Rig;
            if (rig == null)
            {
                return false;
            }

            bool changed = false;

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                RenderTexture rt = rig.GetRenderTexture((CaveFace)i);
                int id = rt != null ? rt.GetInstanceID() : 0;
                Vector2Int size = rt != null ? new Vector2Int(rt.width, rt.height) : Vector2Int.zero;

                if (cachedRenderTextureIds[i] != id || cachedRenderTextureSizes[i] != size)
                {
                    cachedRenderTextureIds[i] = id;
                    cachedRenderTextureSizes[i] = size;
                    changed = true;
                }
            }

            return changed;
        }

        private void RebuildQuads()
        {
            quadsDirty = false;

            CaveRig rig = Rig;
            if (rig == null)
            {
                return;
            }

            EnsureContainer();
            ClearGenerated();

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Texture");
            }

            if (shader == null)
            {
                Debug.LogWarning("[CavePreview] Nenhum shader unlit encontrado; os quads nao serao gerados.", this);
                return;
            }

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                RenderTexture rt = rig.GetRenderTexture(face);
                if (rt == null)
                {
                    continue;
                }

                if (!TryBuildCorners(rig, face, cornerBuffer))
                {
                    continue;
                }

                CreateQuad(face, cornerBuffer, rt, shader);
            }
        }

        /// <summary>
        /// Cantos do quad em espaco local do CaveRoot, na ordem (-,-) (+,-) (-,+) (+,+).
        /// Quando a face tem camera, os cantos saem dos eixos da propria camera: assim a
        /// textura aparece com a mesma orientacao (e o mesmo flip) que o projetor recebe.
        /// </summary>
        private bool TryBuildCorners(CaveRig rig, CaveFace face, Vector3[] corners)
        {
            CaveFaceRect rect = rig.GetFaceRect(face);
            Camera camera = rig.GetCamera(face);

            if (camera != null && rig.TryGetFaceBoundsInCameraSpace(face, camera, out CaveFaceBounds bounds))
            {
                // Puxa o plano na direcao da camera para o quad nao brigar com a parede real.
                float distance = Mathf.Max(0.001f, bounds.distance - quadOffset);
                float scale = distance / bounds.distance;

                for (int i = 0; i < 4; i++)
                {
                    float x = ((i & 1) == 0 ? bounds.left : bounds.right) * scale;
                    float y = ((i & 2) == 0 ? bounds.bottom : bounds.top) * scale;

                    Vector3 world = camera.transform.TransformPoint(new Vector3(x, y, distance));
                    corners[i] = transform.InverseTransformPoint(world);
                }

                return true;
            }

            Vector3 offset = rect.normal * quadOffset;
            for (int i = 0; i < 4; i++)
            {
                corners[i] = rect.Corner(i) + offset;
            }

            return true;
        }

        private void CreateQuad(CaveFace face, Vector3[] corners, RenderTexture texture, Shader shader)
        {
            GameObject go = new GameObject("Face_" + face);
            go.transform.SetParent(container, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            go.hideFlags = QuadHideFlags;
            go.layer = gameObject.layer;

            Mesh mesh = new Mesh
            {
                name = "CavePreview_" + face,
                hideFlags = HideFlags.DontSave,
                vertices = corners,
                uv = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(1f, 0f),
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f)
                },
                triangles = new[] { 0, 2, 1, 2, 3, 1 }
            };

            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            Material material = new Material(shader)
            {
                name = "CavePreview_" + face,
                hideFlags = HideFlags.DontSave
            };

            ConfigureMaterial(material, texture);
            appliedQuadOpacity = quadOpacity;

            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;

            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            quadRenderers.Add(renderer);
            quadFilters.Add(filter);
            quadFaces.Add(face);
            generatedAssets.Add(mesh);
            generatedAssets.Add(material);
        }

        /// <summary>
        /// Reposiciona os vertices dos quads ja existentes, sem recriar objeto, mesh nem
        /// material. E o caminho percorrido enquanto alguem arrasta uma camera no Scene View.
        /// </summary>
        private void RefreshQuadGeometry()
        {
            if (quadFilters.Count == 0)
            {
                return;
            }

            CaveRig rig = Rig;
            if (rig == null)
            {
                return;
            }

            if (!Mathf.Approximately(appliedQuadOpacity, quadOpacity))
            {
                quadsDirty = true;
                return;
            }

            for (int i = 0; i < quadFilters.Count; i++)
            {
                MeshFilter filter = quadFilters[i];
                if (filter == null || filter.sharedMesh == null)
                {
                    quadsDirty = true;
                    return;
                }

                if (!TryBuildCorners(rig, quadFaces[i], cornerBuffer))
                {
                    continue;
                }

                filter.sharedMesh.vertices = cornerBuffer;
                filter.sharedMesh.RecalculateBounds();
            }
        }

        private void ConfigureMaterial(Material material, RenderTexture texture)
        {
            Color tint = new Color(1f, 1f, 1f, Mathf.Clamp01(quadOpacity));

            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }

            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", tint);
            }

            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", tint);
            }

            // Faces viradas para dentro da CAVE: sem culling o preview e visivel de
            // qualquer angulo do Scene View.
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)CullMode.Off);
            }

            if (quadOpacity < 0.999f)
            {
                if (material.HasProperty("_Surface"))
                {
                    material.SetFloat("_Surface", 1f);
                    material.SetFloat("_Blend", 0f);
                    material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    material.SetFloat("_ZWrite", 0f);
                    material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    material.DisableKeyword("_ALPHATEST_ON");
                }

                material.renderQueue = (int)RenderQueue.Transparent;
            }
        }

        private HideFlags QuadHideFlags =>
            hideQuadsInHierarchy
                ? HideFlags.DontSave | HideFlags.NotEditable | HideFlags.HideInHierarchy
                : HideFlags.DontSave | HideFlags.NotEditable;

        private void EnsureContainer()
        {
            if (container != null)
            {
                return;
            }

            // Um container de uma execucao anterior pode ter sobrevivido a um reload de dominio.
            Transform existing = transform.Find(ContainerName);
            if (existing != null)
            {
                container = existing;
                container.gameObject.hideFlags = QuadHideFlags;
                return;
            }

            GameObject go = new GameObject(ContainerName);
            go.transform.SetParent(transform, false);
            go.hideFlags = QuadHideFlags;
            container = go.transform;
        }

        private void ClearGenerated()
        {
            quadRenderers.Clear();
            quadFilters.Clear();
            quadFaces.Clear();

            for (int i = 0; i < generatedAssets.Count; i++)
            {
                SafeDestroy(generatedAssets[i]);
            }

            generatedAssets.Clear();

            if (container == null)
            {
                return;
            }

            for (int i = container.childCount - 1; i >= 0; i--)
            {
                SafeDestroy(container.GetChild(i).gameObject);
            }
        }

        private void DestroyContainerImmediateSafe()
        {
            ClearGenerated();

            if (container != null)
            {
                SafeDestroy(container.gameObject);
                container = null;
            }
        }

        private void DestroyContainerDeferred()
        {
            // Nada de DestroyImmediate aqui: este metodo roda a partir de OnDestroy.
            List<Object> pending = new List<Object>(generatedAssets);
            generatedAssets.Clear();
            quadRenderers.Clear();
            quadFilters.Clear();
            quadFaces.Clear();

            if (container != null)
            {
                pending.Add(container.gameObject);
                container = null;
            }

            if (pending.Count == 0)
            {
                return;
            }

            if (Application.isPlaying)
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i] != null)
                    {
                        Destroy(pending[i]);
                    }
                }

                return;
            }

#if UNITY_EDITOR
            // Destruir de dentro de OnDestroy no editor gera aviso; adia um frame.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                for (int i = 0; i < pending.Count; i++)
                {
                    if (pending[i] != null)
                    {
                        DestroyImmediate(pending[i]);
                    }
                }
            };
#endif
        }

        private static void SafeDestroy(Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(target);
            }
            else
            {
                DestroyImmediate(target);
            }
        }

        private void HandleBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (quadRenderers.Count == 0)
            {
                return;
            }

            bool hide = hideQuadsFromRigCameras && Rig != null && Rig.IsRigCamera(camera);

            for (int i = 0; i < quadRenderers.Count; i++)
            {
                MeshRenderer renderer = quadRenderers[i];
                if (renderer != null)
                {
                    renderer.forceRenderingOff = hide;
                }
            }
        }

        // ------------------------------------------------------------------ gizmos

        private void OnDrawGizmos()
        {
            if (alwaysDrawGizmos)
            {
                DrawGizmos();
            }
        }

        private void OnDrawGizmosSelected()
        {
            if (!alwaysDrawGizmos)
            {
                DrawGizmos();
            }
        }

        private void DrawGizmos()
        {
            CaveRig rig = Rig;
            if (rig == null)
            {
                return;
            }

            Matrix4x4 previousMatrix = Gizmos.matrix;
            Color previousColor = Gizmos.color;

            Gizmos.matrix = transform.localToWorldMatrix;

            if (showWireBox)
            {
                Gizmos.color = boxColor;
                Gizmos.DrawWireCube(rig.CenterLocal, rig.Dimensions);
            }

            if (showFaceOutlines)
            {
                Gizmos.color = faceColor;
                for (int i = 0; i < CaveRig.FaceCount; i++)
                {
                    DrawRectGizmo(rig.GetFaceRect((CaveFace)i));
                }
            }

            if (showEyePoint)
            {
                Gizmos.color = eyeColor;
                Vector3 eye = rig.EyeLocalPosition;
                float size = Mathf.Max(0.1f, rig.Dimensions.magnitude * 0.01f);

                Gizmos.DrawLine(eye - Vector3.right * size, eye + Vector3.right * size);
                Gizmos.DrawLine(eye - Vector3.up * size, eye + Vector3.up * size);
                Gizmos.DrawLine(eye - Vector3.forward * size, eye + Vector3.forward * size);
                Gizmos.DrawWireSphere(eye, size * 0.5f);
            }

            Gizmos.matrix = previousMatrix;

            if (showFrustums || highlightMismatch)
            {
                DrawCoverageGizmos(rig);
            }

            Gizmos.color = previousColor;
        }

        private static void DrawRectGizmo(CaveFaceRect rect)
        {
            Vector3 c0 = rect.Corner(0);
            Vector3 c1 = rect.Corner(1);
            Vector3 c2 = rect.Corner(2);
            Vector3 c3 = rect.Corner(3);

            Gizmos.DrawLine(c0, c1);
            Gizmos.DrawLine(c1, c3);
            Gizmos.DrawLine(c3, c2);
            Gizmos.DrawLine(c2, c0);
        }

        /// <summary>
        /// Desenha, em espaco de mundo, o retangulo que cada camera realmente alcanca na
        /// parede e as arestas do frustum ate ele. E a leitura visual direta da calibracao:
        /// verde encostado na borda da face significa cobertura correta.
        /// </summary>
        private void DrawCoverageGizmos(CaveRig rig)
        {
            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                Camera camera = rig.GetCamera(face);
                if (camera == null)
                {
                    continue;
                }

                CaveFaceRect rect = rig.GetFaceRect(face);
                Vector3 planePoint = transform.TransformPoint(rect.center);
                Plane plane = new Plane(transform.TransformDirection(rect.normal), planePoint);

                Vector3[] hits = new Vector3[4];
                for (int corner = 0; corner < 4; corner++)
                {
                    float u = (corner & 1) == 0 ? 0f : 1f;
                    float v = (corner & 2) == 0 ? 0f : 1f;

                    Ray ray = camera.ViewportPointToRay(new Vector3(u, v, 0f));
                    if (!plane.Raycast(ray, out float enter) || enter <= 0f)
                    {
                        hits = null;
                        break;
                    }

                    hits[corner] = ray.GetPoint(enter);
                }

                if (hits == null)
                {
                    continue;
                }

                CaveCoverage coverage = rig.GetCoverage(face, camera);
                bool withinTolerance = coverage.valid && coverage.WorstPercent <= rig.mismatchTolerancePercent;

                Gizmos.color = !highlightMismatch || withinTolerance ? coverageOkColor : coverageErrorColor;

                Gizmos.DrawLine(hits[0], hits[1]);
                Gizmos.DrawLine(hits[1], hits[3]);
                Gizmos.DrawLine(hits[3], hits[2]);
                Gizmos.DrawLine(hits[2], hits[0]);

                if (showFrustums)
                {
                    Vector3 origin = camera.transform.position;
                    for (int corner = 0; corner < 4; corner++)
                    {
                        Gizmos.DrawLine(origin, hits[corner]);
                    }
                }
            }
        }
    }
}
