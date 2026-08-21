using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    /// <summary>
    /// Captura e aplica snapshot direto no objeto selecionado, sem precisar adicionar
    /// componente nenhum. E o caminho curto: seleciona na Hierarchy, grava o arquivo,
    /// e depois escreve ele de volta quando quiser.
    /// </summary>
    public static class CaveSnapshotMenu
    {
        private const string CaptureItem = "Tools/CAVE/Capturar snapshot do selecionado";
        private const string ApplyItem = "Tools/CAVE/Aplicar snapshot no selecionado...";
        private const string DefaultFolder = "CaveSnapshots";

        // ------------------------------------------------------------------ capturar

        [MenuItem(CaptureItem, false, 10)]
        private static void Capture()
        {
            Transform root = Selection.activeTransform;
            if (root == null)
            {
                return;
            }

            string folder = ResolveDefaultFolder();
            string suggested = root.name + "_"
                               + DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)
                               + ".txt";

            string path = EditorUtility.SaveFilePanel(
                "Gravar snapshot de " + root.name, folder, suggested, "txt");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                CaveSnapshotOptions options = new CaveSnapshotOptions
                {
                    root = root,
                    rig = FindRig(root),
                    includeInactive = true,
                    includeWorldSpace = true,
                    includeComponents = true,
                    includeCameraDetails = true,
                    includeRenderTextureReport = true,
                    includeRectTransforms = true,
                    decimals = 3,
                    maxDepth = -1,
                    note = "Capturado pelo menu Tools > CAVE, a partir da selecao."
                };

                File.WriteAllText(path, CaveSnapshotFormat.Write(options), new UTF8Encoding(true));
                Debug.Log("[CAVE] Snapshot de \"" + root.name + "\" gravado em " + path, root);
            }
            catch (Exception exception)
            {
                Debug.LogError("[CAVE] Falha ao gravar o snapshot: " + exception.Message, root);
            }
        }

        [MenuItem(CaptureItem, true)]
        private static bool CaptureEnabled()
        {
            return Selection.activeTransform != null;
        }

        // ------------------------------------------------------------------ aplicar

        [MenuItem(ApplyItem, false, 11)]
        private static void Apply()
        {
            Transform root = Selection.activeTransform;
            if (root == null)
            {
                return;
            }

            string path = EditorUtility.OpenFilePanel(
                "Snapshot para aplicar em " + root.name, ResolveDefaultFolder(), "txt");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                SnapshotDocument document = CaveSnapshotFormat.Parse(File.ReadAllText(path), path);

                if (document.entries.Count == 0)
                {
                    EditorUtility.DisplayDialog("Aplicar snapshot",
                        "O arquivo nao tem nenhum objeto registrado.\n\n" + path, "Fechar");
                    return;
                }

                SnapshotApplyOptions options = SnapshotApplyOptions.Default;

                if (!EditorUtility.DisplayDialog("Aplicar snapshot",
                        CaveCalibrationGUI.DescribePlan(document, root, options), "Aplicar", "Cancelar"))
                {
                    return;
                }

                SnapshotApplyReport report = CaveCalibrationGUI.ApplyWithUndo(document, root, options);
                Debug.Log("[CAVE] Snapshot aplicado: " + report.Summary + "\n\n"
                          + CaveSnapshotFormat.DescribeApply(report), root);
            }
            catch (Exception exception)
            {
                Debug.LogError("[CAVE] Falha ao aplicar o snapshot: " + exception.Message, root);
            }
        }

        [MenuItem(ApplyItem, true)]
        private static bool ApplyEnabled()
        {
            return Selection.activeTransform != null;
        }

        // ------------------------------------------------------------------ apoio

        private static string ResolveDefaultFolder()
        {
            string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "..", DefaultFolder));

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }

            return folder;
        }

        /// <summary>
        /// O rig so alimenta a secao de proporcoes do relatorio. Sem ele o arquivo sai
        /// igual, menos essa secao, entao vale procurar mas nao vale exigir.
        /// </summary>
        private static CaveRig FindRig(Transform root)
        {
            CaveRig rig = root.GetComponentInParent<CaveRig>();
            if (rig != null)
            {
                return rig;
            }

            rig = root.GetComponentInChildren<CaveRig>(true);
            if (rig != null)
            {
                return rig;
            }

            return UnityEngine.Object.FindFirstObjectByType<CaveRig>(FindObjectsInactive.Include);
        }
    }
}
