using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace CaveJazz.Calibration
{
    /// <summary>Um transform lido de volta de um arquivo de snapshot.</summary>
    public class SnapshotEntry
    {
        public string path;
        public string name;
        public int depth;
        public bool active = true;

        public bool hasLocal;
        public Vector3 localPosition;
        public Vector3 localEuler;
        public Vector3 localScale = Vector3.one;

        public bool hasWorld;
        public Vector3 worldPosition;
        public Vector3 worldEuler;
        public Vector3 lossyScale = Vector3.one;

        public bool hasQuaternions;
        public Quaternion localRotation = Quaternion.identity;
        public Quaternion worldRotation = Quaternion.identity;
    }

    /// <summary>Um arquivo de snapshot inteiro, ja interpretado.</summary>
    public class SnapshotDocument
    {
        public string sourcePath = string.Empty;
        public string date = string.Empty;
        public string unityVersion = string.Empty;
        public string scene = string.Empty;
        public string root = string.Empty;
        public string mode = string.Empty;

        public readonly List<SnapshotEntry> entries = new List<SnapshotEntry>();
        public readonly Dictionary<string, SnapshotEntry> byPath = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);

        public string DisplayName =>
            string.IsNullOrEmpty(sourcePath) ? "(em memoria)" : System.IO.Path.GetFileName(sourcePath);
    }

    /// <summary>Opcoes de captura, preenchidas pelo <see cref="CaveCalibrationSnapshot"/>.</summary>
    public struct CaveSnapshotOptions
    {
        public Transform root;
        public CaveRig rig;
        public bool includeInactive;
        public bool includeWorldSpace;
        public bool includeComponents;
        public bool includeCameraDetails;
        public bool includeRenderTextureReport;
        public bool includeRectTransforms;
        public int decimals;
        public int maxDepth;
        public string note;
    }

    /// <summary>
    /// Escreve, le e compara o formato TXT de snapshot da CAVE.
    ///
    /// O formato e feito para duas plateias ao mesmo tempo: uma pessoa lendo o arquivo
    /// durante a calibracao, e o proprio parser aqui embaixo (o modo diff precisa reler
    /// um arquivo antigo). Por isso a ordem e estavel, as casas decimais sao fixas e a
    /// cultura e sempre invariante: com virgula decimal de pt-BR o parser quebraria.
    /// </summary>
    public static class CaveSnapshotFormat
    {
        public const string HierarchyHeader = "HIERARQUIA";
        private const string MajorRule = "================================================================================";
        private const string MinorRule = "--------------------------------------------------------------------------------";

        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        // ==================================================================== escrita

        public static string Write(CaveSnapshotOptions options)
        {
            if (options.root == null)
            {
                throw new ArgumentException("Snapshot sem transform raiz.", nameof(options));
            }

            int decimals = Mathf.Clamp(options.decimals, 0, 8);
            StringBuilder sb = new StringBuilder(16 * 1024);

            List<Transform> ordered = new List<Transform>();
            Dictionary<Transform, string> paths = new Dictionary<Transform, string>();
            HashSet<string> takenPaths = new HashSet<string>(StringComparer.Ordinal);
            CollectHierarchy(options.root, string.Empty, 0, options, ordered, paths, takenPaths);

            WriteHeader(sb, options, ordered.Count);

            if (options.rig != null)
            {
                WriteGeometry(sb, options.rig, decimals);

                if (options.includeRenderTextureReport)
                {
                    WriteFaceReport(sb, options.rig, decimals);
                }
            }

            WriteHierarchy(sb, options, decimals, ordered, paths);

            sb.AppendLine(MajorRule);
            sb.AppendLine(" FIM DO SNAPSHOT");
            sb.AppendLine(MajorRule);

            return sb.ToString();
        }

        private static void CollectHierarchy(Transform current, string parentPath, int depth,
            CaveSnapshotOptions options, List<Transform> ordered, Dictionary<Transform, string> paths,
            HashSet<string> takenPaths)
        {
            if (current == null)
            {
                return;
            }

            if (!options.includeInactive && !current.gameObject.activeInHierarchy)
            {
                return;
            }

            if (options.maxDepth >= 0 && depth > options.maxDepth)
            {
                return;
            }

            string path = string.IsNullOrEmpty(parentPath) ? current.name : parentPath + "/" + current.name;

            // Irmaos com o mesmo nome existem e nao podem colidir na chave do diff.
            if (takenPaths.Contains(path))
            {
                int suffix = 2;
                string candidate;
                do
                {
                    candidate = path + "#" + suffix.ToString(Culture);
                    suffix++;
                }
                while (takenPaths.Contains(candidate));

                path = candidate;
            }

            ordered.Add(current);
            paths[current] = path;
            takenPaths.Add(path);

            int childCount = current.childCount;
            for (int i = 0; i < childCount; i++)
            {
                CollectHierarchy(current.GetChild(i), path, depth + 1, options, ordered, paths, takenPaths);
            }
        }

        private static void WriteHeader(StringBuilder sb, CaveSnapshotOptions options, int objectCount)
        {
            sb.AppendLine(MajorRule);
            sb.AppendLine(" CAVE CALIBRATION SNAPSHOT");
            sb.AppendLine(MajorRule);
            sb.AppendLine(Field("Data", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Culture)));
            sb.AppendLine(Field("Unity", Application.unityVersion));
            sb.AppendLine(Field("Cena", options.root.gameObject.scene.IsValid()
                ? options.root.gameObject.scene.name
                : "(sem cena)"));
            sb.AppendLine(Field("Raiz", options.root.name));
            sb.AppendLine(Field("Modo", Application.isPlaying ? "Play Mode" : "Edit Mode"));
            sb.AppendLine(Field("Objetos", objectCount.ToString(Culture)));

            if (!string.IsNullOrEmpty(options.note))
            {
                sb.AppendLine(Field("Nota", options.note));
            }
        }

        private static void WriteGeometry(StringBuilder sb, CaveRig rig, int decimals)
        {
            Vector3 dimensions = rig.Dimensions;
            Vector3 eyeLocal = rig.EyeLocalPosition;
            Vector3 eyeWorld = rig.EyeWorldPosition;

            sb.AppendLine(MinorRule);
            sb.AppendLine(" GEOMETRIA DA CAVE");
            sb.AppendLine(MinorRule);
            sb.AppendLine(Field("Escala", N(rig.pixelsPerUnit, decimals) + " px por unidade"));
            sb.AppendLine(Field("Dimensoes", string.Format(Culture, "{0} x {1} x {2} u   (largura x altura x profundidade)",
                N(dimensions.x, decimals), N(dimensions.y, decimals), N(dimensions.z, decimals))));
            sb.AppendLine(Field("Origem", rig.autoDeriveDimensions ? "derivada das Render Textures" : "manual"));
            sb.AppendLine(Field("Piso local Y", N(rig.floorLocalY, decimals)));
            sb.AppendLine(Field("Ponto de olho", string.Format(Culture, "{0} local   ->   {1} world",
                V(eyeLocal, decimals), V(eyeWorld, decimals))));
            sb.AppendLine(Field("Tolerancia", N(rig.mismatchTolerancePercent, 2) + " %"));
        }

        private static void WriteFaceReport(StringBuilder sb, CaveRig rig, int decimals)
        {
            sb.AppendLine(MinorRule);
            sb.AppendLine(" PROPORCOES DE RENDER TEXTURE");
            sb.AppendLine(MinorRule);
            sb.AppendLine(string.Format(Culture, " {0} {1} {2} {3} {4} {5} {6}",
                "Face".PadRight(10),
                "RenderTexture".PadRight(16),
                "RT px".PadRight(12),
                "Asp.RT".PadRight(9),
                "Sensor px".PadRight(14),
                "Asp.Sen".PadRight(9),
                "Status"));

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                CaveFaceBinding binding = rig.GetBinding(face);
                RenderTexture rt = rig.GetRenderTexture(face);
                Camera cam = binding.camera;

                string rtSize = rt != null
                    ? rt.width.ToString(Culture) + "x" + rt.height.ToString(Culture)
                    : "-";
                string rtAspect = rt != null && rt.height > 0
                    ? N((float)rt.width / rt.height, 4)
                    : "-";
                string sensor = cam != null
                    ? N(cam.sensorSize.x, 1) + "x" + N(cam.sensorSize.y, 1)
                    : "-";
                string sensorAspect = cam != null && cam.sensorSize.y > 0f
                    ? N(cam.sensorSize.x / cam.sensorSize.y, 4)
                    : "-";

                sb.AppendLine(string.Format(Culture, " {0} {1} {2} {3} {4} {5} {6}",
                    face.ToString().PadRight(10),
                    (rt != null ? rt.name : "-").PadRight(16),
                    rtSize.PadRight(12),
                    rtAspect.PadRight(9),
                    sensor.PadRight(14),
                    sensorAspect.PadRight(9),
                    FaceStatus(rig, face)));
            }

            sb.AppendLine();

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                WriteFaceDetail(sb, rig, (CaveFace)i, decimals);
            }
        }

        private static void WriteFaceDetail(StringBuilder sb, CaveRig rig, CaveFace face, int decimals)
        {
            CaveFaceBinding binding = rig.GetBinding(face);
            Camera cam = binding.camera;
            RenderTexture rt = rig.GetRenderTexture(face);
            CaveFaceRect rect = rig.GetFaceRect(face);

            sb.AppendLine(string.Format(Culture, " [{0}]  {1}  ->  {2}",
                face,
                cam != null ? cam.name : "(sem camera)",
                rt != null ? rt.name : "(sem render texture)"));

            if (rt != null)
            {
                sb.AppendLine(FaceField("RT", string.Format(Culture, "{0} x {1} px   aspecto {2}",
                    rt.width, rt.height, rt.height > 0 ? N((float)rt.width / rt.height, 4) : "-")));
            }

            sb.AppendLine(FaceField("Parede", string.Format(Culture, "{0} x {1} u   aspecto {2}",
                N(rect.size.x, decimals), N(rect.size.y, decimals), N(rect.Aspect, 4))));

            // Com gate fit "None" a projecao segue o sensor, entao uma diferenca entre o
            // aspecto da parede e o da Render Texture vira esticamento na imagem projetada.
            if (rt != null && rt.height > 0 && rect.Aspect > 0f)
            {
                float rtAspect = (float)rt.width / rt.height;
                float aspectDelta = (rect.Aspect - rtAspect) / rtAspect * 100f;
                if (Mathf.Abs(aspectDelta) > 0.05f)
                {
                    sb.AppendLine(FaceField("Aviso", string.Format(Culture,
                        "aspecto da parede difere do da RT em {0} % - a imagem sai esticada nessa proporcao",
                        S(aspectDelta, 3))));
                }
            }

            if (cam != null)
            {
                sb.AppendLine(FaceField("Sensor atual", string.Format(Culture,
                    "{0} x {1}   focal {2}   shift ({3}, {4})   gate {5}   physical {6}",
                    N(cam.sensorSize.x, 1), N(cam.sensorSize.y, 1), N(cam.focalLength, 2),
                    N(cam.lensShift.x, 4), N(cam.lensShift.y, 4),
                    cam.gateFit, cam.usePhysicalProperties ? "on" : "off")));

                CaveLens lens = rig.GetIdealLens(face);
                if (lens.valid)
                {
                    sb.AppendLine(FaceField("Sensor ideal", string.Format(Culture,
                        "{0} x {1}   focal {2}   shift ({3}, {4})   dist {5} u",
                        N(lens.sensorSize.x, 1), N(lens.sensorSize.y, 1), N(lens.focalLength, 2),
                        N(lens.lensShift.x, 4), N(lens.lensShift.y, 4), N(lens.distance, decimals))));
                }
                else
                {
                    sb.AppendLine(FaceField("Sensor ideal", "nao calculavel (face fora do campo da camera)"));
                }

                CaveCoverage coverage = rig.GetCoverage(face);
                if (coverage.valid)
                {
                    Vector2 percent = coverage.DeltaPercent;
                    sb.AppendLine(FaceField("Cobertura", string.Format(Culture,
                        "{0} x {1} u   desvio {2} % / {3} %   deslocamento ({4}, {5}) u",
                        N(coverage.covered.x, decimals), N(coverage.covered.y, decimals),
                        S(percent.x, 2), S(percent.y, 2),
                        N(coverage.offset.x, decimals), N(coverage.offset.y, decimals))));
                }
                else
                {
                    sb.AppendLine(FaceField("Cobertura", "nao medivel (a camera nao alcanca o plano da face)"));
                }
            }

            sb.AppendLine(FaceField("Status", FaceStatus(rig, face)));
            sb.AppendLine();
        }

        private static string FaceStatus(CaveRig rig, CaveFace face)
        {
            CaveFaceBinding binding = rig.GetBinding(face);
            if (binding.camera == null)
            {
                return "SEM CAMERA";
            }

            if (rig.GetRenderTexture(face) == null)
            {
                return "SEM RENDER TEXTURE";
            }

            CaveCoverage coverage = rig.GetCoverage(face);
            if (!coverage.valid)
            {
                return "NAO MEDIVEL";
            }

            float worst = coverage.WorstPercent;
            return worst <= rig.mismatchTolerancePercent
                ? string.Format(Culture, "OK ({0} %)", S(worst, 2))
                : string.Format(Culture, "DIVERGENTE ({0} %)", S(worst, 2));
        }

        private static void WriteHierarchy(StringBuilder sb, CaveSnapshotOptions options, int decimals,
            List<Transform> ordered, Dictionary<Transform, string> paths)
        {
            sb.AppendLine(MinorRule);
            sb.AppendLine(" " + HierarchyHeader);
            sb.AppendLine(MinorRule);

            for (int i = 0; i < ordered.Count; i++)
            {
                Transform t = ordered[i];
                string path = paths[t];
                int depth = DepthOf(path);
                string indent = new string(' ', depth * 2);
                string detail = indent + "    ";

                sb.AppendLine(string.Format(Culture, "{0}[{1}] {2}  ({3})",
                    indent, depth, t.name, t.gameObject.activeSelf ? "ativo" : "inativo"));

                sb.AppendLine(detail + "path   " + path);

                sb.AppendLine(detail + string.Format(Culture, "local  P{0}  R{1}  S{2}",
                    V(t.localPosition, decimals),
                    V(t.localRotation.eulerAngles, decimals),
                    V(t.localScale, decimals)));

                if (options.includeWorldSpace)
                {
                    sb.AppendLine(detail + string.Format(Culture, "world  P{0}  R{1}  L{2}",
                        V(t.position, decimals),
                        V(t.rotation.eulerAngles, decimals),
                        V(t.lossyScale, decimals)));
                }

                sb.AppendLine(detail + string.Format(Culture, "quat   local{0}  world{1}",
                    Q(t.localRotation), Q(t.rotation)));

                if (options.includeRectTransforms && t is RectTransform rectTransform)
                {
                    sb.AppendLine(detail + string.Format(Culture,
                        "rect   size({0}, {1})  anchorMin({2}, {3})  anchorMax({4}, {5})  pivot({6}, {7})  anchoredPos({8}, {9})",
                        N(rectTransform.sizeDelta.x, decimals), N(rectTransform.sizeDelta.y, decimals),
                        N(rectTransform.anchorMin.x, decimals), N(rectTransform.anchorMin.y, decimals),
                        N(rectTransform.anchorMax.x, decimals), N(rectTransform.anchorMax.y, decimals),
                        N(rectTransform.pivot.x, decimals), N(rectTransform.pivot.y, decimals),
                        N(rectTransform.anchoredPosition.x, decimals), N(rectTransform.anchoredPosition.y, decimals)));
                }

                if (options.includeCameraDetails)
                {
                    Camera camera = t.GetComponent<Camera>();
                    if (camera != null)
                    {
                        AppendCamera(sb, detail, camera, decimals);
                    }
                }

                if (options.includeComponents)
                {
                    sb.AppendLine(detail + "comp   " + ComponentList(t));
                }
            }
        }

        private static void AppendCamera(StringBuilder sb, string detail, Camera camera, int decimals)
        {
            float aspect = camera.aspect;
            float horizontal = aspect > 0f
                ? Camera.VerticalToHorizontalFieldOfView(camera.fieldOfView, aspect)
                : 0f;

            sb.AppendLine(detail + string.Format(Culture,
                "cam    physical {0}  sensor({1}, {2})  focal {3}  shift({4}, {5})  gate {6}",
                camera.usePhysicalProperties ? "on" : "off",
                N(camera.sensorSize.x, 1), N(camera.sensorSize.y, 1),
                N(camera.focalLength, 2),
                N(camera.lensShift.x, 4), N(camera.lensShift.y, 4),
                camera.gateFit));

            sb.AppendLine(detail + string.Format(Culture,
                "cam    vFOV {0}  hFOV {1}  aspect {2}  near {3}  far {4}  depth {5}  mask 0x{6}",
                N(camera.fieldOfView, 3), N(horizontal, 3), N(aspect, 4),
                N(camera.nearClipPlane, decimals), N(camera.farClipPlane, decimals),
                N(camera.depth, 1),
                camera.cullingMask.ToString("X8", Culture)));

            RenderTexture target = camera.targetTexture;
            sb.AppendLine(detail + (target != null
                ? string.Format(Culture, "cam    target {0}  {1}x{2}  aspecto {3}",
                    target.name, target.width, target.height,
                    target.height > 0 ? N((float)target.width / target.height, 4) : "-")
                : "cam    target (nenhum)"));
        }

        private static string ComponentList(Transform t)
        {
            Component[] components = t.GetComponents<Component>();
            StringBuilder sb = new StringBuilder();

            for (int i = 0; i < components.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                // Um componente nulo aqui e um script perdido (o prefab da CAVE carrega
                // sobras de HDAdditionalCameraData). Registrar em vez de estourar.
                sb.Append(components[i] != null ? components[i].GetType().Name : "<MissingScript>");
            }

            return sb.Length > 0 ? sb.ToString() : "(nenhum)";
        }

        private static int DepthOf(string path)
        {
            int depth = 0;
            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] == '/')
                {
                    depth++;
                }
            }

            return depth;
        }

        // ==================================================================== formatacao

        private static string Field(string label, string value)
        {
            return " " + label.PadRight(14) + ": " + value;
        }

        private static string FaceField(string label, string value)
        {
            return "     " + label.PadRight(13) + ": " + value;
        }

        private static string N(float value, int decimals)
        {
            // -0.000 e ruido puro num arquivo feito para ser comparado.
            if (Mathf.Abs(value) < 5e-7f)
            {
                value = 0f;
            }

            return value.ToString("F" + decimals.ToString(Culture), Culture);
        }

        /// <summary>Numero com sinal explicito, para colunas de desvio.</summary>
        private static string S(float value, int decimals)
        {
            string text = N(value, decimals);
            return value > 0f && !text.StartsWith("-", StringComparison.Ordinal) ? "+" + text : text;
        }

        private static string V(Vector3 value, int decimals)
        {
            return string.Format(Culture, "({0}, {1}, {2})",
                N(value.x, decimals).PadLeft(10),
                N(value.y, decimals).PadLeft(10),
                N(value.z, decimals).PadLeft(10));
        }

        private static string Q(Quaternion value)
        {
            return string.Format(Culture, "({0}, {1}, {2}, {3})",
                N(value.x, 6), N(value.y, 6), N(value.z, 6), N(value.w, 6));
        }

        // ==================================================================== leitura

        /// <summary>
        /// Le de volta um arquivo escrito por <see cref="Write"/>. So a secao de hierarquia
        /// alimenta o diff; o cabecalho vira metadado e a tabela de faces e ignorada.
        /// </summary>
        public static SnapshotDocument Parse(string text, string sourcePath = null)
        {
            SnapshotDocument doc = new SnapshotDocument();
            if (sourcePath != null)
            {
                doc.sourcePath = sourcePath;
            }

            if (string.IsNullOrEmpty(text))
            {
                return doc;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inHierarchy = false;
            SnapshotEntry current = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                if (!inHierarchy)
                {
                    if (trimmed == HierarchyHeader)
                    {
                        inHierarchy = true;
                        continue;
                    }

                    ReadHeaderField(doc, trimmed);
                    continue;
                }

                if (trimmed.Length == 0 || trimmed.StartsWith("---", StringComparison.Ordinal)
                    || trimmed.StartsWith("===", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    SnapshotEntry entry = ReadEntryHeader(trimmed);
                    if (entry != null)
                    {
                        current = entry;
                    }

                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                if (trimmed.StartsWith("path ", StringComparison.Ordinal))
                {
                    current.path = trimmed.Substring(5).Trim();
                    if (!string.IsNullOrEmpty(current.path) && !doc.byPath.ContainsKey(current.path))
                    {
                        doc.entries.Add(current);
                        doc.byPath.Add(current.path, current);
                    }
                }
                else if (trimmed.StartsWith("local ", StringComparison.Ordinal))
                {
                    if (TryReadTriple(trimmed, out Vector3 p, out Vector3 r, out Vector3 s))
                    {
                        current.localPosition = p;
                        current.localEuler = r;
                        current.localScale = s;
                        current.hasLocal = true;
                    }
                }
                else if (trimmed.StartsWith("world ", StringComparison.Ordinal))
                {
                    if (TryReadTriple(trimmed, out Vector3 p, out Vector3 r, out Vector3 s))
                    {
                        current.worldPosition = p;
                        current.worldEuler = r;
                        current.lossyScale = s;
                        current.hasWorld = true;
                    }
                }
                else if (trimmed.StartsWith("quat ", StringComparison.Ordinal))
                {
                    List<float[]> groups = ReadGroups(trimmed);
                    if (groups.Count >= 2 && groups[0].Length == 4 && groups[1].Length == 4)
                    {
                        current.localRotation = ToQuaternion(groups[0]);
                        current.worldRotation = ToQuaternion(groups[1]);
                        current.hasQuaternions = true;
                    }
                }
            }

            return doc;
        }

        private static void ReadHeaderField(SnapshotDocument doc, string trimmed)
        {
            int separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                return;
            }

            string label = trimmed.Substring(0, separator).Trim();
            string value = trimmed.Substring(separator + 1).Trim();

            switch (label)
            {
                case "Data": doc.date = value; break;
                case "Unity": doc.unityVersion = value; break;
                case "Cena": doc.scene = value; break;
                case "Raiz": doc.root = value; break;
                case "Modo": doc.mode = value; break;
            }
        }

        private static SnapshotEntry ReadEntryHeader(string trimmed)
        {
            int close = trimmed.IndexOf(']');
            if (close <= 1)
            {
                return null;
            }

            string depthText = trimmed.Substring(1, close - 1);
            if (!int.TryParse(depthText, NumberStyles.Integer, Culture, out int depth))
            {
                // "[Frontal]" e cabecalho da tabela de faces, nao um objeto.
                return null;
            }

            SnapshotEntry entry = new SnapshotEntry { depth = depth };

            string rest = trimmed.Substring(close + 1).Trim();
            int marker = rest.LastIndexOf("  (", StringComparison.Ordinal);
            if (marker >= 0)
            {
                entry.active = rest.IndexOf("(ativo)", marker, StringComparison.Ordinal) >= 0;
                rest = rest.Substring(0, marker).Trim();
            }

            entry.name = rest;
            entry.path = rest;
            return entry;
        }

        private static bool TryReadTriple(string line, out Vector3 a, out Vector3 b, out Vector3 c)
        {
            a = Vector3.zero;
            b = Vector3.zero;
            c = Vector3.one;

            List<float[]> groups = ReadGroups(line);
            if (groups.Count < 3)
            {
                return false;
            }

            if (groups[0].Length != 3 || groups[1].Length != 3 || groups[2].Length != 3)
            {
                return false;
            }

            a = new Vector3(groups[0][0], groups[0][1], groups[0][2]);
            b = new Vector3(groups[1][0], groups[1][1], groups[1][2]);
            c = new Vector3(groups[2][0], groups[2][1], groups[2][2]);
            return true;
        }

        /// <summary>Extrai todo grupo "( ... )" da linha como uma lista de floats.</summary>
        private static List<float[]> ReadGroups(string line)
        {
            List<float[]> groups = new List<float[]>();
            int index = 0;

            while (index < line.Length)
            {
                int open = line.IndexOf('(', index);
                if (open < 0)
                {
                    break;
                }

                int close = line.IndexOf(')', open + 1);
                if (close < 0)
                {
                    break;
                }

                string body = line.Substring(open + 1, close - open - 1);
                string[] parts = body.Split(',');
                float[] values = new float[parts.Length];
                bool ok = true;

                for (int i = 0; i < parts.Length; i++)
                {
                    if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, Culture, out values[i]))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    groups.Add(values);
                }

                index = close + 1;
            }

            return groups;
        }

        private static Quaternion ToQuaternion(float[] v) => new Quaternion(v[0], v[1], v[2], v[3]);

        // ==================================================================== diff

        /// <summary>
        /// Compara dois snapshots. A rotacao e comparada pelo quaternion (angulo entre as
        /// duas orientacoes) e nao pelos angulos de Euler, que tem mais de uma representacao
        /// para a mesma rotacao e produziriam diferencas falsas.
        /// </summary>
        public static string Diff(SnapshotDocument before, SnapshotDocument after,
            float positionTolerance = 0.001f, float angleTolerance = 0.01f, float scaleTolerance = 0.001f)
        {
            if (before == null || after == null)
            {
                throw new ArgumentNullException(before == null ? nameof(before) : nameof(after));
            }

            StringBuilder sb = new StringBuilder(8 * 1024);

            sb.AppendLine(MajorRule);
            sb.AppendLine(" CAVE CALIBRATION DIFF");
            sb.AppendLine(MajorRule);
            sb.AppendLine(Field("Gerado", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Culture)));
            sb.AppendLine(Field("A (antes)", before.DisplayName + "   " + before.date));
            sb.AppendLine(Field("B (depois)", after.DisplayName + "   " + after.date));
            sb.AppendLine(Field("Tolerancia", string.Format(Culture,
                "posicao {0} u   rotacao {1} graus   escala {2}",
                N(positionTolerance, 4), N(angleTolerance, 4), N(scaleTolerance, 4))));
            sb.AppendLine(MinorRule);

            int changed = 0;
            int added = 0;
            int removed = 0;
            int unchanged = 0;

            for (int i = 0; i < after.entries.Count; i++)
            {
                SnapshotEntry b = after.entries[i];

                if (!before.byPath.TryGetValue(b.path, out SnapshotEntry a))
                {
                    added++;
                    sb.AppendLine(" + " + b.path);
                    if (b.hasLocal)
                    {
                        sb.AppendLine("     local P" + V(b.localPosition, 3) + "  R" + V(b.localEuler, 3)
                                      + "  S" + V(b.localScale, 3));
                    }

                    continue;
                }

                List<string> differences = CompareEntries(a, b, positionTolerance, angleTolerance, scaleTolerance);
                if (differences.Count == 0)
                {
                    unchanged++;
                    continue;
                }

                changed++;
                sb.AppendLine(" ~ " + b.path);
                for (int d = 0; d < differences.Count; d++)
                {
                    sb.AppendLine("     " + differences[d]);
                }
            }

            for (int i = 0; i < before.entries.Count; i++)
            {
                SnapshotEntry a = before.entries[i];
                if (!after.byPath.ContainsKey(a.path))
                {
                    removed++;
                    sb.AppendLine(" - " + a.path);
                }
            }

            if (changed == 0 && added == 0 && removed == 0)
            {
                sb.AppendLine(" Nenhuma diferenca acima da tolerancia.");
            }

            sb.AppendLine(MinorRule);
            sb.AppendLine(string.Format(Culture,
                " Resumo: {0} alterado(s), {1} adicionado(s), {2} removido(s), {3} inalterado(s)",
                changed, added, removed, unchanged));
            sb.AppendLine(MajorRule);

            return sb.ToString();
        }

        private static List<string> CompareEntries(SnapshotEntry a, SnapshotEntry b,
            float positionTolerance, float angleTolerance, float scaleTolerance)
        {
            List<string> differences = new List<string>();

            if (a.active != b.active)
            {
                differences.Add(string.Format(Culture, "ativo      {0} -> {1}",
                    a.active ? "ativo" : "inativo", b.active ? "ativo" : "inativo"));
            }

            if (a.hasLocal && b.hasLocal)
            {
                AppendVectorDiff(differences, "local P", a.localPosition, b.localPosition, positionTolerance);
                AppendVectorDiff(differences, "local S", a.localScale, b.localScale, scaleTolerance);
            }

            if (a.hasWorld && b.hasWorld)
            {
                AppendVectorDiff(differences, "world P", a.worldPosition, b.worldPosition, positionTolerance);
                AppendVectorDiff(differences, "world L", a.lossyScale, b.lossyScale, scaleTolerance);
            }

            if (a.hasQuaternions && b.hasQuaternions)
            {
                AppendRotationDiff(differences, "local R", a.localRotation, b.localRotation,
                    a.localEuler, b.localEuler, angleTolerance);
                AppendRotationDiff(differences, "world R", a.worldRotation, b.worldRotation,
                    a.worldEuler, b.worldEuler, angleTolerance);
            }
            else if (a.hasLocal && b.hasLocal)
            {
                AppendVectorDiff(differences, "local R", a.localEuler, b.localEuler, angleTolerance);
            }

            return differences;
        }

        private static void AppendVectorDiff(List<string> differences, string label,
            Vector3 a, Vector3 b, float tolerance)
        {
            Vector3 delta = b - a;
            if (Mathf.Abs(delta.x) <= tolerance && Mathf.Abs(delta.y) <= tolerance && Mathf.Abs(delta.z) <= tolerance)
            {
                return;
            }

            differences.Add(string.Format(Culture, "{0}    {1} -> {2}   d({3}, {4}, {5})   |d| {6}",
                label, V(a, 3), V(b, 3),
                S(delta.x, 3), S(delta.y, 3), S(delta.z, 3),
                N(delta.magnitude, 3)));
        }

        private static void AppendRotationDiff(List<string> differences, string label,
            Quaternion a, Quaternion b, Vector3 eulerA, Vector3 eulerB, float tolerance)
        {
            float angle = Quaternion.Angle(a, b);
            if (angle <= tolerance)
            {
                return;
            }

            differences.Add(string.Format(Culture, "{0}    {1} -> {2}   angulo {3} graus",
                label, V(eulerA, 3), V(eulerB, 3), N(angle, 3)));
        }
    }
}
