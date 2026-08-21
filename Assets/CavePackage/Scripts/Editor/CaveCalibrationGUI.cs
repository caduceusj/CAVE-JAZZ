using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    /// <summary>
    /// Desenho da tabela de faces e as acoes de calibracao, compartilhados entre o
    /// Inspector do <see cref="CaveRig"/> e a janela Window > CAVE > Calibracao.
    /// </summary>
    public static class CaveCalibrationGUI
    {
        private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        private static readonly Color OkColor = new Color(0.35f, 0.8f, 0.4f);
        private static readonly Color WarnColor = new Color(0.95f, 0.55f, 0.2f);
        private static readonly Color ErrorColor = new Color(0.95f, 0.35f, 0.35f);

        private struct FacePlan
        {
            public CaveFace face;
            public Camera camera;
            public CaveLens lens;
        }

        // ================================================================== tabela

        public static void DrawFaceTable(CaveRig rig)
        {
            if (rig == null)
            {
                EditorGUILayout.HelpBox("Nenhum CaveRig encontrado na cena.", MessageType.Info);
                return;
            }

            rig.EnsureBindings();

            Vector3 dimensions = rig.Dimensions;
            EditorGUILayout.LabelField("Sala", string.Format(Culture,
                "{0:F3} x {1:F3} x {2:F3} u   ({3:F0} px/u)   olho {4}",
                dimensions.x, dimensions.y, dimensions.z, rig.pixelsPerUnit,
                FormatVector(rig.EyeLocalPosition)));

            EditorGUILayout.Space(2f);

            DrawHeaderRow();

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                DrawFaceRow(rig, (CaveFace)i);
            }
        }

        private static void DrawHeaderRow()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Face", EditorStyles.miniBoldLabel, GUILayout.Width(70f));
                GUILayout.Label("RenderTexture", EditorStyles.miniBoldLabel, GUILayout.Width(110f));
                GUILayout.Label("RT px", EditorStyles.miniBoldLabel, GUILayout.Width(80f));
                GUILayout.Label("Asp.", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
                GUILayout.Label("Sensor", EditorStyles.miniBoldLabel, GUILayout.Width(95f));
                GUILayout.Label("Asp.", EditorStyles.miniBoldLabel, GUILayout.Width(50f));
                GUILayout.Label("Desvio", EditorStyles.miniBoldLabel, GUILayout.Width(65f));
                GUILayout.Label("Status", EditorStyles.miniBoldLabel, GUILayout.MinWidth(90f));
            }
        }

        private static void DrawFaceRow(CaveRig rig, CaveFace face)
        {
            CaveFaceBinding binding = rig.GetBinding(face);
            Camera camera = binding.camera;
            RenderTexture rt = rig.GetRenderTexture(face);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(face.ToString(), EditorStyles.miniLabel, GUILayout.Width(70f))
                    && camera != null)
                {
                    Selection.activeGameObject = camera.gameObject;
                    EditorGUIUtility.PingObject(camera.gameObject);
                }

                GUILayout.Label(rt != null ? rt.name : "-", EditorStyles.miniLabel, GUILayout.Width(110f));

                GUILayout.Label(rt != null ? rt.width + "x" + rt.height : "-",
                    EditorStyles.miniLabel, GUILayout.Width(80f));

                GUILayout.Label(rt != null && rt.height > 0
                        ? ((float)rt.width / rt.height).ToString("F3", Culture)
                        : "-",
                    EditorStyles.miniLabel, GUILayout.Width(50f));

                GUILayout.Label(camera != null
                        ? camera.sensorSize.x.ToString("F0", Culture) + "x" + camera.sensorSize.y.ToString("F0", Culture)
                        : "-",
                    EditorStyles.miniLabel, GUILayout.Width(95f));

                GUILayout.Label(camera != null && camera.sensorSize.y > 0f
                        ? (camera.sensorSize.x / camera.sensorSize.y).ToString("F3", Culture)
                        : "-",
                    EditorStyles.miniLabel, GUILayout.Width(50f));

                CaveCoverage coverage = rig.GetCoverage(face);
                Color previous = GUI.color;

                if (camera == null || rt == null)
                {
                    GUI.color = WarnColor;
                    GUILayout.Label("-", EditorStyles.miniLabel, GUILayout.Width(65f));
                    GUILayout.Label(camera == null ? "sem camera" : "sem render texture",
                        EditorStyles.miniLabel, GUILayout.MinWidth(90f));
                }
                else if (!coverage.valid)
                {
                    GUI.color = ErrorColor;
                    GUILayout.Label("-", EditorStyles.miniLabel, GUILayout.Width(65f));
                    GUILayout.Label("nao medivel", EditorStyles.miniLabel, GUILayout.MinWidth(90f));
                }
                else
                {
                    float worst = coverage.WorstPercent;
                    bool ok = worst <= rig.mismatchTolerancePercent;
                    GUI.color = ok ? OkColor : ErrorColor;

                    GUILayout.Label(worst.ToString("F2", Culture) + "%",
                        EditorStyles.miniLabel, GUILayout.Width(65f));
                    GUILayout.Label(ok ? "OK" : "DIVERGENTE",
                        EditorStyles.miniLabel, GUILayout.MinWidth(90f));
                }

                GUI.color = previous;
            }
        }

        // ================================================================== acoes

        public static void DrawRigActions(CaveRig rig)
        {
            if (rig == null)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Resolver faces"))
                {
                    Undo.RecordObject(rig, "Resolver faces da CAVE");
                    rig.ResolveBindings();
                    EditorUtility.SetDirty(rig);
                }

                if (GUILayout.Button("Aplicar calibracao"))
                {
                    ApplyCalibration(rig);
                }
            }

            EditorGUILayout.HelpBox(
                "\"Aplicar calibracao\" reescreve sensor, distancia focal, lens shift e gate fit de cada " +
                "camera para cobrir exatamente a sua parede a partir do ponto de olho. " +
                "Posicao e rotacao das cameras nao sao tocadas, e a acao e desfeita com Ctrl+Z.",
                MessageType.None);
        }

        /// <summary>
        /// Reescreve a lente fisica de cada camera do rig. Nunca roda sozinho: sempre a
        /// partir de um clique, com dialogo de confirmacao listando o que muda.
        /// </summary>
        public static bool ApplyCalibration(CaveRig rig)
        {
            if (rig == null)
            {
                return false;
            }

            rig.EnsureBindings();

            List<FacePlan> plan = new List<FacePlan>();
            StringBuilder summary = new StringBuilder();

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                Camera camera = rig.GetCamera(face);
                if (camera == null)
                {
                    continue;
                }

                CaveLens lens = rig.GetIdealLens(face, camera);
                if (!lens.valid)
                {
                    summary.AppendLine(string.Format(Culture,
                        "{0} ({1}): nao calculavel, a face esta fora do campo da camera.", face, camera.name));
                    continue;
                }

                plan.Add(new FacePlan { face = face, camera = camera, lens = lens });

                summary.AppendLine(string.Format(Culture,
                    "{0} ({1})\n    sensor {2:F1} x {3:F1}  ->  {4:F1} x {5:F1}\n    focal  {6:F2}  ->  {7:F2}\n    shift  ({8:F4}, {9:F4})  ->  ({10:F4}, {11:F4})",
                    face, camera.name,
                    camera.sensorSize.x, camera.sensorSize.y,
                    lens.sensorSize.x, lens.sensorSize.y,
                    camera.focalLength, lens.focalLength,
                    camera.lensShift.x, camera.lensShift.y,
                    lens.lensShift.x, lens.lensShift.y));
            }

            if (plan.Count == 0)
            {
                EditorUtility.DisplayDialog("Calibracao da CAVE",
                    "Nenhuma face tem camera com lente calculavel.\n\n" + summary, "Fechar");
                return false;
            }

            if (!EditorUtility.DisplayDialog("Aplicar calibracao da CAVE",
                    summary + "\nPosicao e rotacao nao sao alteradas.", "Aplicar", "Cancelar"))
            {
                return false;
            }

            Undo.SetCurrentGroupName("Aplicar calibracao da CAVE");
            int undoGroup = Undo.GetCurrentGroup();

            for (int i = 0; i < plan.Count; i++)
            {
                FacePlan item = plan[i];
                Camera camera = item.camera;

                Undo.RecordObject(camera, "Aplicar calibracao da CAVE");

                camera.usePhysicalProperties = true;
                // Gate fit "None" faz o sensor mandar na projecao; qualquer outro modo
                // reajusta o frustum ao aspecto do alvo e desfaz o calculo.
                camera.gateFit = Camera.GateFitMode.None;
                camera.sensorSize = item.lens.sensorSize;
                camera.focalLength = item.lens.focalLength;
                camera.lensShift = item.lens.lensShift;

                EditorUtility.SetDirty(camera);

                if (PrefabUtility.IsPartOfPrefabInstance(camera))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            SceneView.RepaintAll();

            Debug.Log(string.Format(Culture, "[CAVE] Calibracao aplicada em {0} camera(s).", plan.Count), rig);
            return true;
        }

        // ================================================================== snapshot

        public static void DrawSnapshotActions(CaveCalibrationSnapshot snapshot)
        {
            if (snapshot == null)
            {
                EditorGUILayout.HelpBox(
                    "Nenhum CaveCalibrationSnapshot na cena. Adicione o componente ao CaveRoot " +
                    "para capturar o TXT.", MessageType.Info);
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Capturar snapshot (TXT)"))
                {
                    Capture(snapshot);
                }

                using (new EditorGUI.DisabledScope(!HasLastSnapshot(snapshot)))
                {
                    if (GUILayout.Button("Abrir ultimo", GUILayout.Width(90f)))
                    {
                        EditorUtility.OpenWithDefaultApp(snapshot.LastSnapshotPath);
                    }
                }

                if (GUILayout.Button("Abrir pasta", GUILayout.Width(90f)))
                {
                    string folder = snapshot.ResolveOutputFolder();
                    System.IO.Directory.CreateDirectory(folder);
                    EditorUtility.RevealInFinder(folder);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Comparar com snapshot..."))
                {
                    Compare(snapshot);
                }

                if (GUILayout.Button("Aplicar snapshot..."))
                {
                    Apply(snapshot);
                }
            }

            if (HasLastSnapshot(snapshot))
            {
                EditorGUILayout.LabelField("Ultimo arquivo", snapshot.LastSnapshotPath, EditorStyles.miniLabel);
            }
        }

        private static bool HasLastSnapshot(CaveCalibrationSnapshot snapshot)
        {
            return !string.IsNullOrEmpty(snapshot.LastSnapshotPath)
                   && System.IO.File.Exists(snapshot.LastSnapshotPath);
        }

        public static void Capture(CaveCalibrationSnapshot snapshot)
        {
            try
            {
                Undo.RecordObject(snapshot, "Capturar snapshot da CAVE");
                string path = snapshot.CaptureToFile();
                EditorUtility.SetDirty(snapshot);

                Debug.Log("[CAVE] Snapshot gravado em " + path, snapshot);
            }
            catch (System.Exception exception)
            {
                Debug.LogError("[CAVE] Falha ao gravar o snapshot: " + exception.Message, snapshot);
            }
        }

        public static void Compare(CaveCalibrationSnapshot snapshot)
        {
            string startFolder = snapshot.ResolveOutputFolder();
            if (!System.IO.Directory.Exists(startFolder))
            {
                startFolder = Application.dataPath;
            }

            string previous = EditorUtility.OpenFilePanel(
                "Snapshot anterior para comparar", startFolder, "txt");

            if (string.IsNullOrEmpty(previous))
            {
                return;
            }

            try
            {
                string diffPath = snapshot.CompareWithFileToFile(previous);
                Debug.Log("[CAVE] Diff gravado em " + diffPath + "\n\n"
                          + System.IO.File.ReadAllText(diffPath), snapshot);

                EditorUtility.OpenWithDefaultApp(diffPath);
            }
            catch (System.Exception exception)
            {
                Debug.LogError("[CAVE] Falha ao comparar snapshots: " + exception.Message, snapshot);
            }
        }

        /// <summary>
        /// Le um TXT gravado antes e escreve os valores de volta. O dialogo diz exatamente
        /// o que vai e o que nao vai ser escrito, antes de encostar em qualquer objeto.
        /// </summary>
        public static void Apply(CaveCalibrationSnapshot snapshot)
        {
            string startFolder = snapshot.ResolveOutputFolder();
            if (!System.IO.Directory.Exists(startFolder))
            {
                startFolder = Application.dataPath;
            }

            string path = EditorUtility.OpenFilePanel("Snapshot para aplicar", startFolder, "txt");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                SnapshotDocument document = CaveCalibrationSnapshot.LoadDocument(path);
                Transform root = snapshot.ResolvedTarget;

                if (!EditorUtility.DisplayDialog("Aplicar snapshot",
                        DescribePlan(document, root, snapshot.BuildApplyOptions()), "Aplicar", "Cancelar"))
                {
                    return;
                }

                SnapshotApplyReport report = ApplyWithUndo(document, root, snapshot.BuildApplyOptions());
                Debug.Log("[CAVE] Snapshot aplicado: " + report.Summary + "\n\n"
                          + CaveSnapshotFormat.DescribeApply(report), root);
            }
            catch (System.Exception exception)
            {
                Debug.LogError("[CAVE] Falha ao aplicar o snapshot: " + exception.Message, snapshot);
            }
        }

        /// <summary>Aplica registrando Undo em cada objeto antes de ele mudar.</summary>
        public static SnapshotApplyReport ApplyWithUndo(SnapshotDocument document, Transform root,
            SnapshotApplyOptions options)
        {
            const string label = "Aplicar snapshot da CAVE";

            Undo.SetCurrentGroupName(label);
            int undoGroup = Undo.GetCurrentGroup();

            SnapshotApplyReport report = CaveSnapshotFormat.Apply(document, root, options,
                target => Undo.RecordObject(target, label));

            Undo.CollapseUndoOperations(undoGroup);
            SceneView.RepaintAll();

            return report;
        }

        /// <summary>Texto do dialogo: o que sera escrito e o que nao sera.</summary>
        public static string DescribePlan(SnapshotDocument document, Transform root,
            SnapshotApplyOptions options)
        {
            List<string> writes = new List<string>();
            List<string> skips = new List<string>();

            (options.applyTransforms ? writes : skips).Add(
                "posicao e rotacao (" + (options.useWorldSpace ? "world" : "local") + ")");
            (options.applyScale ? writes : skips).Add("escala (sempre local)");
            (options.applyCameraSettings ? writes : skips).Add("configuracoes de camera");
            (options.applyRectTransforms ? writes : skips).Add("RectTransform");
            (options.applyActiveState ? writes : skips).Add("estado ativo");
            skips.Add("referencias de Render Texture");

            string text = "Arquivo: " + document.DisplayName + "\n"
                          + "Capturado em: " + document.date + "\n"
                          + "Objetos no arquivo: " + document.entries.Count + "\n\n"
                          + "Alvo: " + root.name + " e os filhos dele\n\n"
                          + "Vai escrever: " + string.Join(", ", writes) + "\n"
                          + "Nao vai escrever: " + string.Join(", ", skips) + "\n\n"
                          + "Desfeito de uma vez com Ctrl+Z.";

            return text;
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(Culture, "({0:F3}, {1:F3}, {2:F3})", value.x, value.y, value.z);
        }
    }
}
