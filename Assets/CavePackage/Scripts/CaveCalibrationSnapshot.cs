using System;
using System.IO;
using System.Text;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace CaveJazz.Calibration
{
    /// <summary>
    /// Registra a posicao e a rotacao de um GameObject e de todos os seus filhos num TXT,
    /// junto com os dados de camera e de Render Texture que interessam para calibrar a CAVE.
    ///
    /// Funciona em Edit Mode (pelo botao no Inspector) e em build rodando na CAVE de verdade
    /// (por tecla de atalho), por isso o caminho de captura nao usa nenhuma API de editor.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("CAVE/Cave Calibration Snapshot")]
    public class CaveCalibrationSnapshot : MonoBehaviour
    {
        [Header("Alvo")]
        [Tooltip("Raiz da hierarquia a registrar. Vazio = este proprio GameObject.")]
        public Transform target;

        [Tooltip("Rig usado na secao de geometria e de proporcoes. Vazio = procura no proprio objeto, " +
                 "nos pais e, por ultimo, na cena.")]
        public CaveRig rig;

        [Header("Arquivo")]
        [Tooltip("Pasta de saida. Caminho relativo e resolvido a partir da raiz do projeto " +
                 "(ao lado de Assets/). Caminho absoluto e usado como esta.")]
        public string outputFolder = "CaveSnapshots";

        [Tooltip("Em build, grava em Application.persistentDataPath em vez da pasta do executavel, " +
                 "que costuma ser somente leitura.")]
        public bool usePersistentDataPathInBuilds = true;

        [Tooltip("Prefixo do arquivo. O carimbo de data e hora e acrescentado automaticamente.")]
        public string fileNamePrefix = "CaveSnapshot";

        [Tooltip("Texto livre gravado no cabecalho. Util para marcar 'antes do ajuste', 'projetor 3 trocado', etc.")]
        [TextArea(1, 3)]
        public string note = string.Empty;

        [Header("Conteudo")]
        public bool includeInactive = true;
        public bool includeWorldSpace = true;
        public bool includeComponents = true;
        public bool includeCameraDetails = true;
        public bool includeRenderTextureReport = true;
        public bool includeRectTransforms = true;

        [Tooltip("Casas decimais dos valores de transform.")]
        [Range(0, 8)]
        public int decimals = 3;

        [Tooltip("Profundidade maxima da hierarquia. -1 = sem limite.")]
        [Min(-1)]
        public int maxDepth = -1;

        [Header("Aplicar")]
        [Tooltip("Escreve posicao e rotacao de volta nos objetos.")]
        public bool applyTransforms = true;

        [Tooltip("Aplica posicao e rotacao em espaco de mundo. Desligado, usa o espaco local. " +
                 "Escala e sempre local: o Unity nao tem setter de escala global.")]
        public bool applyInWorldSpace = true;

        public bool applyScale = true;

        [Tooltip("Desligado por padrao: aplicar um arquivo antigo poderia sumir com objetos.")]
        public bool applyActiveState;

        [Tooltip("Escreve sensor, focal, lens shift, gate fit, recorte e culling mask das cameras.")]
        public bool applyCameraSettings = true;

        public bool applyRectTransformSettings = true;

        [Header("Tolerancias do diff")]
        [Min(0f)] public float positionTolerance = 0.001f;
        [Min(0f)] public float angleTolerance = 0.01f;
        [Min(0f)] public float scaleTolerance = 0.001f;

        [Header("Captura em runtime")]
        [Tooltip("Captura uma vez assim que a cena entra em Play.")]
        public bool captureOnStart;

#if ENABLE_INPUT_SYSTEM
        [Tooltip("Tecla que dispara a captura durante o Play. None = desligado.")]
        public Key runtimeCaptureKey = Key.None;
#elif ENABLE_LEGACY_INPUT_MANAGER
        [Tooltip("Tecla que dispara a captura durante o Play. None = desligado.")]
        public KeyCode runtimeCaptureKey = KeyCode.None;
#endif

        [SerializeField, HideInInspector]
        private string lastSnapshotPath = string.Empty;

        /// <summary>Caminho do ultimo arquivo gravado, ou vazio se nenhum foi gravado ainda.</summary>
        public string LastSnapshotPath => lastSnapshotPath;

        public Transform ResolvedTarget => target != null ? target : transform;

        // ------------------------------------------------------------------ ciclo de vida

        private void Start()
        {
            if (captureOnStart && Application.isPlaying)
            {
                CaptureToFile();
            }
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

#if ENABLE_INPUT_SYSTEM
            if (runtimeCaptureKey != Key.None
                && Keyboard.current != null
                && Keyboard.current[runtimeCaptureKey].wasPressedThisFrame)
            {
                CaptureToFile();
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (runtimeCaptureKey != KeyCode.None && Input.GetKeyDown(runtimeCaptureKey))
            {
                CaptureToFile();
            }
#endif
        }

        // ------------------------------------------------------------------ captura

        /// <summary>Monta o relatorio sem gravar nada.</summary>
        public string Capture()
        {
            return CaveSnapshotFormat.Write(BuildOptions());
        }

        /// <summary>Monta o relatorio e grava num TXT novo. Devolve o caminho completo.</summary>
        public string CaptureToFile()
        {
            string report = Capture();
            string path = BuildFilePath();

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, report, new UTF8Encoding(true));

            lastSnapshotPath = path;
            return path;
        }

        public CaveSnapshotOptions BuildOptions()
        {
            return new CaveSnapshotOptions
            {
                root = ResolvedTarget,
                rig = ResolveRig(),
                includeInactive = includeInactive,
                includeWorldSpace = includeWorldSpace,
                includeComponents = includeComponents,
                includeCameraDetails = includeCameraDetails,
                includeRenderTextureReport = includeRenderTextureReport,
                includeRectTransforms = includeRectTransforms,
                decimals = decimals,
                maxDepth = maxDepth,
                note = note
            };
        }

        /// <summary>
        /// Procura o rig no campo, no proprio objeto, nos pais e por fim na cena.
        /// Comparacoes explicitas com null porque UnityEngine.Object tem null "falso"
        /// e o operador ?? nao o enxerga.
        /// </summary>
        public CaveRig ResolveRig()
        {
            if (rig != null)
            {
                return rig;
            }

            CaveRig found = GetComponent<CaveRig>();
            if (found != null)
            {
                return found;
            }

            found = GetComponentInParent<CaveRig>();
            if (found != null)
            {
                return found;
            }

            Transform resolvedTarget = ResolvedTarget;
            if (resolvedTarget != null)
            {
                found = resolvedTarget.GetComponentInParent<CaveRig>();
                if (found != null)
                {
                    return found;
                }

                found = resolvedTarget.GetComponentInChildren<CaveRig>(true);
                if (found != null)
                {
                    return found;
                }
            }

#if UNITY_2022_2_OR_NEWER
            return FindFirstObjectByType<CaveRig>(FindObjectsInactive.Include);
#else
            return FindObjectOfType<CaveRig>(true);
#endif
        }

        // ------------------------------------------------------------------ aplicar

        public SnapshotApplyOptions BuildApplyOptions()
        {
            return new SnapshotApplyOptions
            {
                applyTransforms = applyTransforms,
                useWorldSpace = applyInWorldSpace,
                applyScale = applyScale,
                applyActiveState = applyActiveState,
                applyCameraSettings = applyCameraSettings,
                applyRectTransforms = applyRectTransformSettings,
                includeInactive = includeInactive,
                maxDepth = maxDepth
            };
        }

        /// <summary>
        /// Le um TXT gravado antes e escreve os valores de volta no alvo e nos filhos dele.
        /// <paramref name="recordUndo"/> e chamado antes de cada objeto mudar; no editor,
        /// passe <c>obj => Undo.RecordObject(obj, "...")</c>.
        /// </summary>
        public SnapshotApplyReport ApplyFromFile(string snapshotPath,
            Action<UnityEngine.Object> recordUndo = null)
        {
            SnapshotDocument document = LoadDocument(snapshotPath);
            return CaveSnapshotFormat.Apply(document, ResolvedTarget, BuildApplyOptions(), recordUndo);
        }

        // ------------------------------------------------------------------ diff

        /// <summary>Compara um TXT gravado antes com o estado atual da cena.</summary>
        public string CompareWithFile(string previousSnapshotPath)
        {
            SnapshotDocument before = LoadDocument(previousSnapshotPath);
            SnapshotDocument after = CaveSnapshotFormat.Parse(Capture());
            after.sourcePath = "(estado atual da cena)";

            return CaveSnapshotFormat.Diff(before, after, positionTolerance, angleTolerance, scaleTolerance);
        }

        /// <summary>Compara dois TXT ja gravados.</summary>
        public string CompareFiles(string beforePath, string afterPath)
        {
            return CaveSnapshotFormat.Diff(
                LoadDocument(beforePath),
                LoadDocument(afterPath),
                positionTolerance, angleTolerance, scaleTolerance);
        }

        /// <summary>Compara e grava o resultado ao lado do snapshot mais recente.</summary>
        public string CompareWithFileToFile(string previousSnapshotPath)
        {
            string diff = CompareWithFile(previousSnapshotPath);
            string path = BuildFilePath("_diff");

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, diff, new UTF8Encoding(true));

            return path;
        }

        public static SnapshotDocument LoadDocument(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("Snapshot nao encontrado.", path ?? string.Empty);
            }

            return CaveSnapshotFormat.Parse(File.ReadAllText(path), path);
        }

        // ------------------------------------------------------------------ caminhos

        public string ResolveOutputFolder()
        {
            string folder = string.IsNullOrEmpty(outputFolder) ? "CaveSnapshots" : outputFolder.Trim();
            if (folder.Length == 0)
            {
                folder = "CaveSnapshots";
            }

            if (Path.IsPathRooted(folder))
            {
                return Path.GetFullPath(folder);
            }

            string root = !Application.isEditor && usePersistentDataPathInBuilds
                ? Application.persistentDataPath
                : Path.Combine(Application.dataPath, "..");

            return Path.GetFullPath(Path.Combine(root, folder));
        }

        private string BuildFilePath(string suffix = "")
        {
            string folder = ResolveOutputFolder();
            string prefix = string.IsNullOrEmpty(fileNamePrefix) ? "CaveSnapshot" : fileNamePrefix.Trim();
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", System.Globalization.CultureInfo.InvariantCulture);

            string path = Path.Combine(folder, prefix + "_" + stamp + suffix + ".txt");

            // Duas capturas no mesmo segundo nao podem sobrescrever uma a outra.
            int counter = 2;
            while (File.Exists(path))
            {
                path = Path.Combine(folder, prefix + "_" + stamp + suffix + "_" + counter + ".txt");
                counter++;
            }

            return path;
        }
    }
}
