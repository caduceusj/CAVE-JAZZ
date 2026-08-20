using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace CaveJazz.Calibration
{
    /// <summary>
    /// As cinco faces projetadas da CAVE, nomeadas como no projeto.
    /// </summary>
    public enum CaveFace
    {
        Frontal = 0,
        Traseira = 1,
        Esquerda = 2,
        Direita = 3,
        Piso = 4
    }

    /// <summary>
    /// Liga uma face da CAVE a uma camera do rig e a Render Texture que ela alimenta.
    /// </summary>
    [Serializable]
    public class CaveFaceBinding
    {
        public CaveFace face;
        public Camera camera;
        public RenderTexture renderTexture;

        public bool HasCamera => camera != null;
        public bool HasRenderTexture => renderTexture != null;
    }

    /// <summary>
    /// Retangulo de uma face, em espaco local do CaveRoot.
    /// <see cref="right"/> e <see cref="up"/> sao unitarios e <see cref="normal"/> aponta
    /// para dentro da CAVE (na direcao do ponto de olho).
    /// </summary>
    public struct CaveFaceRect
    {
        public CaveFace face;
        public Vector3 center;
        public Vector3 right;
        public Vector3 up;
        public Vector3 normal;
        public Vector2 size;

        public float Aspect => size.y > 0f ? size.x / size.y : 0f;

        /// <summary>Cantos na ordem: 0 = (-,-), 1 = (+,-), 2 = (-,+), 3 = (+,+).</summary>
        public Vector3 Corner(int index)
        {
            float sx = (index & 1) == 0 ? -0.5f : 0.5f;
            float sy = (index & 2) == 0 ? -0.5f : 0.5f;
            return center + right * (size.x * sx) + up * (size.y * sy);
        }
    }

    /// <summary>
    /// Limites do retangulo de uma face vistos do espaco da camera, a uma distancia
    /// de referencia. Em unidades Unity.
    /// </summary>
    public struct CaveFaceBounds
    {
        public float left;
        public float right;
        public float bottom;
        public float top;
        public float distance;

        public float Width => right - left;
        public float Height => top - bottom;
    }

    /// <summary>
    /// Parametros de lente fisica que fazem uma camera cobrir exatamente uma face,
    /// a partir do ponto de olho. Frustum assimetrico (off-axis), que e a projecao
    /// correta para CAVE quando o olho nao esta no centro da parede.
    /// </summary>
    public struct CaveLens
    {
        public bool valid;
        public Vector2 sensorSize;
        public float focalLength;
        public Vector2 lensShift;
        public float distance;

        public float Aspect => sensorSize.y > 0f ? sensorSize.x / sensorSize.y : 0f;
    }

    /// <summary>
    /// O que a camera realmente cobre no plano da face, comparado ao que deveria cobrir.
    /// </summary>
    public struct CaveCoverage
    {
        public bool valid;
        public Vector2 covered;
        public Vector2 target;
        public Vector2 offset;

        public Vector2 Delta => covered - target;

        public Vector2 DeltaPercent => new Vector2(
            target.x > 0f ? (covered.x - target.x) / target.x * 100f : 0f,
            target.y > 0f ? (covered.y - target.y) / target.y * 100f : 0f);

        public float WorstPercent
        {
            get
            {
                Vector2 d = DeltaPercent;
                return Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.y));
            }
        }
    }

    /// <summary>
    /// Fonte unica de verdade da geometria da CAVE. Fica no CaveRoot e deriva as
    /// dimensoes da sala a partir das Render Textures das cinco cameras, com override
    /// manual. Nao altera nada sozinho: so calcula e mede.
    ///
    /// Referencial local (o mesmo que as cameras do prefab ja usam):
    /// +Z = Frontal, -Z = Traseira, +X = Direita, -X = Esquerda, -Y = Piso.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("CAVE/Cave Rig")]
    public class CaveRig : MonoBehaviour
    {
        public const int FaceCount = 5;

        [Header("Escala")]
        [Tooltip("Quantos pixels de Render Texture equivalem a 1 unidade Unity. " +
                 "100 reproduz a calibracao atual do projeto (parede de 940 px = 9.40 u).")]
        [Min(0.0001f)]
        public float pixelsPerUnit = 100f;

        [Header("Dimensoes")]
        [Tooltip("Deriva largura/altura/profundidade das Render Textures ligadas as faces.")]
        public bool autoDeriveDimensions = true;

        [Tooltip("Usado quando 'autoDeriveDimensions' esta desligado. X = largura, Y = altura, Z = profundidade.")]
        public Vector3 manualDimensions = new Vector3(14.02f, 9.42f, 26.25f);

        [Tooltip("Altura local do piso da CAVE dentro do CaveRoot.")]
        public float floorLocalY;

        [Header("Ponto de olho")]
        [Tooltip("Deriva o ponto de olho da posicao local media das cameras do rig.")]
        public bool autoDeriveEye = true;

        [Tooltip("Usado quando 'autoDeriveEye' esta desligado.")]
        public Vector3 manualEyeLocalPosition = new Vector3(0f, 4.7f, 0f);

        [Header("Faces")]
        [Tooltip("Resolve as cameras e Render Textures por nome sempre que o componente e habilitado.")]
        public bool autoResolveBindings = true;

        [SerializeField]
        private List<CaveFaceBinding> bindings = new List<CaveFaceBinding>();

        [Header("Tolerancia")]
        [Tooltip("Desvio de cobertura, em porcento, a partir do qual uma face e considerada divergente.")]
        [Min(0f)]
        public float mismatchTolerancePercent = 1f;

        public IReadOnlyList<CaveFaceBinding> Bindings => bindings;

        // ------------------------------------------------------------------ ciclo de vida

        private void OnEnable()
        {
            EnsureBindings();
            if (autoResolveBindings)
            {
                ResolveBindings();
            }
        }

        private void OnValidate()
        {
            if (pixelsPerUnit < 0.0001f)
            {
                pixelsPerUnit = 0.0001f;
            }

            EnsureBindings();
        }

        // ------------------------------------------------------------------ bindings

        /// <summary>Garante exatamente uma entrada por face, na ordem do enum.</summary>
        public void EnsureBindings()
        {
            if (bindings == null)
            {
                bindings = new List<CaveFaceBinding>(FaceCount);
            }

            for (int i = 0; i < FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;

                if (i >= bindings.Count)
                {
                    bindings.Add(new CaveFaceBinding { face = face });
                    continue;
                }

                if (bindings[i] == null)
                {
                    bindings[i] = new CaveFaceBinding { face = face };
                    continue;
                }

                bindings[i].face = face;
            }

            while (bindings.Count > FaceCount)
            {
                bindings.RemoveAt(bindings.Count - 1);
            }
        }

        public CaveFaceBinding GetBinding(CaveFace face)
        {
            EnsureBindings();
            return bindings[(int)face];
        }

        public Camera GetCamera(CaveFace face) => GetBinding(face).camera;

        public RenderTexture GetRenderTexture(CaveFace face)
        {
            CaveFaceBinding binding = GetBinding(face);
            if (binding.renderTexture != null)
            {
                return binding.renderTexture;
            }

            return binding.camera != null ? binding.camera.targetTexture : null;
        }

        public bool IsRigCamera(Camera candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            EnsureBindings();
            for (int i = 0; i < bindings.Count; i++)
            {
                if (bindings[i].camera == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Todas as cameras sob a raiz, e nao apenas as cinco ligadas a faces. E este o
        /// conjunto certo para mexer em culling mask: a CamSender nao alimenta face nenhuma
        /// mas renderiza a cena com far clip 20000.
        /// </summary>
        public Camera[] GetRigCameras()
        {
            return GetComponentsInChildren<Camera>(true);
        }

        /// <summary>
        /// Casa cameras filhas com faces pelo nome da Render Texture e, em seguida, pelo
        /// nome da propria camera. Cameras sem correspondencia (CamSender, por exemplo)
        /// sao ignoradas de proposito.
        /// </summary>
        public void ResolveBindings()
        {
            EnsureBindings();

            Camera[] cameras = GetComponentsInChildren<Camera>(true);

            // Uma camera so pode servir a uma face; sem isso um nome ambiguo casaria duas vezes.
            HashSet<Camera> taken = new HashSet<Camera>();
            for (int i = 0; i < FaceCount; i++)
            {
                if (bindings[i].camera != null)
                {
                    taken.Add(bindings[i].camera);
                }
            }

            for (int i = 0; i < FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                CaveFaceBinding binding = bindings[i];

                // Uma camera atribuida a mao fica como esta. So preenche o que esta vazio
                // (ou ficou vazio porque o objeto foi destruido).
                if (binding.camera == null)
                {
                    binding.camera = FindCameraForFace(face, cameras, taken);
                    if (binding.camera != null)
                    {
                        taken.Add(binding.camera);
                    }
                }

                if (binding.camera != null && binding.renderTexture == null)
                {
                    binding.renderTexture = binding.camera.targetTexture;
                }
            }
        }

        private static Camera FindCameraForFace(CaveFace face, Camera[] cameras, HashSet<Camera> taken)
        {
            // A Render Texture e o sinal mais forte: RT_Frontal so pode ser a face frontal.
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (taken.Contains(camera))
                {
                    continue;
                }

                if (camera.targetTexture != null && MatchesFace(camera.targetTexture.name, face))
                {
                    return camera;
                }
            }

            // Sem Render Texture ainda: cai para o nome da camera. CamSender nao casa com
            // nenhuma face e continua de fora, que e o que se quer.
            for (int i = 0; i < cameras.Length; i++)
            {
                Camera camera = cameras[i];
                if (taken.Contains(camera))
                {
                    continue;
                }

                if (MatchesFace(camera.name, face))
                {
                    return camera;
                }
            }

            return null;
        }

        private static bool MatchesFace(string rawName, CaveFace face)
        {
            string name = Simplify(rawName);
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            switch (face)
            {
                case CaveFace.Frontal:
                    return name.Contains("frontal") || name.Contains("front");
                case CaveFace.Traseira:
                    return name.Contains("traseira") || name.Contains("tras") || name.Contains("back");
                case CaveFace.Esquerda:
                    return name.Contains("esquerda") || name.Contains("esq") || name.Contains("left");
                case CaveFace.Direita:
                    return name.Contains("direita") || name.Contains("dir") || name.Contains("right");
                case CaveFace.Piso:
                    return name.Contains("piso") || name.Contains("chao") || name.Contains("floor")
                           || name.Contains("ground");
                default:
                    return false;
            }
        }

        /// <summary>Minusculas sem acento, para casar "CamTraseira", "RT_Chao", "cam_direita".</summary>
        private static string Simplify(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = char.ToLowerInvariant(value[i]);
                switch (c)
                {
                    case 'á': case 'à': case 'â': case 'ã': case 'ä': c = 'a'; break;
                    case 'é': case 'è': case 'ê': case 'ë': c = 'e'; break;
                    case 'í': case 'ì': case 'î': case 'ï': c = 'i'; break;
                    case 'ó': case 'ò': case 'ô': case 'õ': case 'ö': c = 'o'; break;
                    case 'ú': case 'ù': case 'û': case 'ü': c = 'u'; break;
                    case 'ç': c = 'c'; break;
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        // ------------------------------------------------------------------ dimensoes

        public Vector3 Dimensions
        {
            get
            {
                if (!autoDeriveDimensions)
                {
                    return manualDimensions;
                }

                Vector3 derived = manualDimensions;

                float width = PixelsToUnits(RenderTextureWidth(CaveFace.Frontal, CaveFace.Traseira));
                float depth = PixelsToUnits(RenderTextureWidth(CaveFace.Direita, CaveFace.Esquerda));
                float height = PixelsToUnits(RenderTextureHeight(CaveFace.Frontal, CaveFace.Traseira));

                if (height <= 0f)
                {
                    height = PixelsToUnits(RenderTextureHeight(CaveFace.Direita, CaveFace.Esquerda));
                }

                // O piso cobre largura x profundidade e serve de rede de seguranca
                // quando as paredes verticais nao estao ligadas.
                RenderTexture floor = GetRenderTexture(CaveFace.Piso);
                if (floor != null)
                {
                    if (depth <= 0f)
                    {
                        depth = PixelsToUnits(floor.width);
                    }

                    if (width <= 0f)
                    {
                        width = PixelsToUnits(floor.height);
                    }
                }

                if (width > 0f)
                {
                    derived.x = width;
                }

                if (height > 0f)
                {
                    derived.y = height;
                }

                if (depth > 0f)
                {
                    derived.z = depth;
                }

                return derived;
            }
        }

        public float Width => Dimensions.x;
        public float Height => Dimensions.y;
        public float Depth => Dimensions.z;

        private float PixelsToUnits(float pixels) => pixels > 0f ? pixels / pixelsPerUnit : 0f;

        /// <summary>
        /// Comparacoes explicitas com null: uma referencia de asset perdida vira null
        /// "falso" em UnityEngine.Object, que o operador ?? nao enxerga.
        /// </summary>
        private RenderTexture FirstRenderTexture(CaveFace primary, CaveFace fallback)
        {
            RenderTexture rt = GetRenderTexture(primary);
            if (rt != null)
            {
                return rt;
            }

            rt = GetRenderTexture(fallback);
            return rt != null ? rt : null;
        }

        private int RenderTextureWidth(CaveFace primary, CaveFace fallback)
        {
            RenderTexture rt = FirstRenderTexture(primary, fallback);
            return rt != null ? rt.width : 0;
        }

        private int RenderTextureHeight(CaveFace primary, CaveFace fallback)
        {
            RenderTexture rt = FirstRenderTexture(primary, fallback);
            return rt != null ? rt.height : 0;
        }

        // ------------------------------------------------------------------ ponto de olho

        /// <summary>Ponto de olho em espaco local do CaveRoot.</summary>
        public Vector3 EyeLocalPosition
        {
            get
            {
                if (!autoDeriveEye)
                {
                    return manualEyeLocalPosition;
                }

                EnsureBindings();

                Vector3 sum = Vector3.zero;
                int count = 0;

                for (int i = 0; i < bindings.Count; i++)
                {
                    Camera camera = bindings[i].camera;
                    if (camera == null)
                    {
                        continue;
                    }

                    sum += transform.InverseTransformPoint(camera.transform.position);
                    count++;
                }

                return count > 0 ? sum / count : manualEyeLocalPosition;
            }
        }

        public Vector3 EyeWorldPosition => transform.TransformPoint(EyeLocalPosition);

        // ------------------------------------------------------------------ geometria das faces

        public CaveFaceRect GetFaceRect(CaveFace face)
        {
            Vector3 size = Dimensions;
            float w = size.x;
            float h = size.y;
            float d = size.z;
            float centerY = floorLocalY + h * 0.5f;

            CaveFaceRect rect = new CaveFaceRect { face = face, up = Vector3.up };

            switch (face)
            {
                case CaveFace.Frontal:
                    rect.center = new Vector3(0f, centerY, d * 0.5f);
                    rect.right = Vector3.right;
                    rect.normal = Vector3.back;
                    rect.size = new Vector2(w, h);
                    break;

                case CaveFace.Traseira:
                    rect.center = new Vector3(0f, centerY, -d * 0.5f);
                    rect.right = Vector3.left;
                    rect.normal = Vector3.forward;
                    rect.size = new Vector2(w, h);
                    break;

                case CaveFace.Direita:
                    rect.center = new Vector3(w * 0.5f, centerY, 0f);
                    rect.right = Vector3.back;
                    rect.normal = Vector3.left;
                    rect.size = new Vector2(d, h);
                    break;

                case CaveFace.Esquerda:
                    rect.center = new Vector3(-w * 0.5f, centerY, 0f);
                    rect.right = Vector3.forward;
                    rect.normal = Vector3.right;
                    rect.size = new Vector2(d, h);
                    break;

                case CaveFace.Piso:
                    // Eixos alinhados com CamPiso (direita = +Z, cima = -X): sem isso o
                    // aspecto do piso sairia invertido no relatorio (0.534 em vez de 1.872).
                    rect.center = new Vector3(0f, floorLocalY, 0f);
                    rect.right = Vector3.forward;
                    rect.up = Vector3.left;
                    rect.normal = Vector3.up;
                    rect.size = new Vector2(d, w);
                    break;
            }

            return rect;
        }

        public Vector3 FaceCornerWorld(CaveFaceRect rect, int index)
        {
            return transform.TransformPoint(rect.Corner(index));
        }

        /// <summary>Centro geometrico da CAVE, em espaco local.</summary>
        public Vector3 CenterLocal => new Vector3(0f, floorLocalY + Height * 0.5f, 0f);

        // ------------------------------------------------------------------ lente ideal

        /// <summary>
        /// Sensor, distancia focal e lens shift que fazem a camera da face cobrir
        /// exatamente o retangulo dessa face, a partir de onde a camera esta.
        /// O sensor sai em "pixels" (unidades x <see cref="pixelsPerUnit"/>) para o valor
        /// bater de frente com a resolucao da Render Texture no relatorio.
        /// </summary>
        public CaveLens GetIdealLens(CaveFace face)
        {
            Camera camera = GetCamera(face);
            return camera != null ? GetIdealLens(face, camera) : default;
        }

        public CaveLens GetIdealLens(CaveFace face, Camera camera)
        {
            CaveLens lens = default;

            if (!TryGetFaceBoundsInCameraSpace(face, camera, out CaveFaceBounds bounds))
            {
                return lens;
            }

            float spanX = bounds.right - bounds.left;
            float spanY = bounds.top - bounds.bottom;

            lens.valid = true;
            lens.distance = bounds.distance;
            lens.sensorSize = new Vector2(spanX, spanY) * pixelsPerUnit;
            lens.focalLength = bounds.distance * pixelsPerUnit;
            lens.lensShift = new Vector2(
                (bounds.right + bounds.left) * 0.5f / spanX,
                (bounds.top + bounds.bottom) * 0.5f / spanY);

            return lens;
        }

        /// <summary>
        /// Projeta o retangulo da face no espaco da camera e devolve os limites do frustum
        /// minimo que o contem, a uma distancia de referencia. Serve tanto para calcular a
        /// lente ideal quanto para montar os quads do preview no lugar certo.
        /// </summary>
        public bool TryGetFaceBoundsInCameraSpace(CaveFace face, Camera camera, out CaveFaceBounds bounds)
        {
            bounds = default;
            if (camera == null)
            {
                return false;
            }

            CaveFaceRect rect = GetFaceRect(face);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float depthSum = 0f;

            for (int i = 0; i < 4; i++)
            {
                Vector3 world = FaceCornerWorld(rect, i);
                Vector3 inCamera = camera.transform.InverseTransformPoint(world);

                if (inCamera.z <= Mathf.Epsilon)
                {
                    // Um canto atras da camera: nao existe frustum que cubra a face.
                    return false;
                }

                // Normalizado a 1 unidade de distancia, para o calculo continuar valido
                // mesmo se a camera nao estiver perpendicular a parede.
                float nx = inCamera.x / inCamera.z;
                float ny = inCamera.y / inCamera.z;

                minX = Mathf.Min(minX, nx);
                maxX = Mathf.Max(maxX, nx);
                minY = Mathf.Min(minY, ny);
                maxY = Mathf.Max(maxY, ny);
                depthSum += inCamera.z;
            }

            float distance = depthSum / 4f;

            bounds.left = minX * distance;
            bounds.right = maxX * distance;
            bounds.bottom = minY * distance;
            bounds.top = maxY * distance;
            bounds.distance = distance;

            return bounds.right - bounds.left > Mathf.Epsilon
                   && bounds.top - bounds.bottom > Mathf.Epsilon;
        }

        // ------------------------------------------------------------------ cobertura real

        /// <summary>
        /// Mede, no plano da face, o retangulo que a camera realmente cobre com a
        /// projecao que ela tem agora. E o "antes" que o botao de calibrar corrige.
        /// </summary>
        public CaveCoverage GetCoverage(CaveFace face)
        {
            Camera camera = GetCamera(face);
            return camera != null ? GetCoverage(face, camera) : default;
        }

        public CaveCoverage GetCoverage(CaveFace face, Camera camera)
        {
            CaveCoverage coverage = default;
            if (camera == null)
            {
                return coverage;
            }

            CaveFaceRect rect = GetFaceRect(face);
            coverage.target = rect.size;

            Vector3 planePoint = transform.TransformPoint(rect.center);
            Vector3 planeNormal = transform.TransformDirection(rect.normal);
            Plane plane = new Plane(planeNormal, planePoint);

            Vector3 rightWorld = transform.TransformDirection(rect.right).normalized;
            Vector3 upWorld = transform.TransformDirection(rect.up).normalized;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;

            for (int i = 0; i < 4; i++)
            {
                float u = (i & 1) == 0 ? 0f : 1f;
                float v = (i & 2) == 0 ? 0f : 1f;

                Ray ray = camera.ViewportPointToRay(new Vector3(u, v, 0f));
                if (!plane.Raycast(ray, out float enter) || enter <= 0f)
                {
                    return coverage;
                }

                Vector3 relative = ray.GetPoint(enter) - planePoint;
                float x = Vector3.Dot(relative, rightWorld);
                float y = Vector3.Dot(relative, upWorld);

                minX = Mathf.Min(minX, x);
                maxX = Mathf.Max(maxX, x);
                minY = Mathf.Min(minY, y);
                maxY = Mathf.Max(maxY, y);
            }

            coverage.valid = true;
            coverage.covered = new Vector2(maxX - minX, maxY - minY);
            coverage.offset = new Vector2((maxX + minX) * 0.5f, (maxY + minY) * 0.5f);
            return coverage;
        }

        public bool IsFaceCalibrated(CaveFace face)
        {
            CaveCoverage coverage = GetCoverage(face);
            return coverage.valid && coverage.WorstPercent <= mismatchTolerancePercent;
        }
    }
}
