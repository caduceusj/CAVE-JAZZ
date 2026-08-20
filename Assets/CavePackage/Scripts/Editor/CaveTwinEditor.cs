using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    [CustomEditor(typeof(CaveTwin))]
    public class CaveTwinEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            CaveTwin twin = (CaveTwin)target;
            CaveRig rig = twin.ResolveSource();

            EditorGUILayout.Space(8f);

            if (rig == null)
            {
                EditorGUILayout.HelpBox(
                    "Nenhum CaveRig encontrado. Adicione o componente Cave Rig ao CaveRoot "
                    + "para o gemeo saber as medidas da sala.", MessageType.Warning);
                return;
            }

            DrawStatus(twin, rig);

            EditorGUILayout.Space(6f);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Gerar / atualizar gemeo"))
                {
                    CaveTwinBuilder.Build(twin);
                }

                using (new EditorGUI.DisabledScope(!IsBuilt(twin)))
                {
                    if (GUILayout.Button("Remover", GUILayout.Width(90f))
                        && EditorUtility.DisplayDialog("Remover gemeo digital",
                            "Remover as telas, a estrutura e as referencias? "
                            + "A configuracao do componente e os materiais em disco continuam.",
                            "Remover", "Cancelar"))
                    {
                        CaveTwinBuilder.Remove(twin);
                    }
                }
            }

            using (new EditorGUI.DisabledScope(!IsBuilt(twin)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Enquadrar no Scene View"))
                    {
                        Frame(twin);
                    }

                    if (GUILayout.Button("Olhar do ponto de olho"))
                    {
                        LookFromEye(twin, rig);
                    }
                }

                if (GUILayout.Button("Salvar como prefab"))
                {
                    SaveAsPrefab(twin);
                }
            }
        }

        private static void DrawStatus(CaveTwin twin, CaveRig rig)
        {
            Vector3 dimensions = rig.Dimensions;

            EditorGUILayout.LabelField("Sala", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F3} x {1:F3} x {2:F3} u   aberta em cima",
                dimensions.x, dimensions.y, dimensions.z));

            EditorGUILayout.LabelField("Referencia", string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F2} m = {1:F3} u", twin.humanHeightMeters, twin.HumanHeightInUnits));

            if (!IsBuilt(twin))
            {
                EditorGUILayout.HelpBox("O gemeo ainda nao foi gerado.", MessageType.Info);
                return;
            }

            int layer = LayerMask.NameToLayer(twin.twinLayerName);
            if (layer < 0)
            {
                return;
            }

            // Se alguma camera do rig voltar a enxergar a camada, a maquete reaparece
            // dentro das Render Textures. Vale avisar antes de virar surpresa no Play.
            int bit = 1 << layer;
            int leaking = 0;
            Camera[] cameras = rig.GetRigCameras();

            for (int i = 0; i < cameras.Length; i++)
            {
                if ((cameras[i].cullingMask & bit) != 0)
                {
                    leaking++;
                }
            }

            if (leaking > 0)
            {
                EditorGUILayout.HelpBox(
                    leaking + " camera(s) do rig ainda enxergam a camada \"" + twin.twinLayerName
                    + "\". O gemeo vai aparecer dentro do render da CAVE. "
                    + "Gere de novo para corrigir o culling mask.", MessageType.Warning);
            }
        }

        private static bool IsBuilt(CaveTwin twin)
        {
            return twin.transform.Find(CaveTwin.ScreensGroup) != null;
        }

        private static void Frame(CaveTwin twin)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                return;
            }

            Renderer[] renderers = twin.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            view.Frame(bounds, false);
        }

        private static void LookFromEye(CaveTwin twin, CaveRig rig)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                return;
            }

            // O olho e definido em espaco local do rig, e o gemeo espelha esse mesmo
            // referencial: da para reaproveitar a coordenada direto.
            Vector3 eye = twin.transform.TransformPoint(rig.EyeLocalPosition);
            CaveFaceRect front = rig.GetFaceRect(CaveFace.Frontal);
            Vector3 target = twin.transform.TransformPoint(front.center);

            Vector3 direction = target - eye;
            if (direction.sqrMagnitude < 0.0001f)
            {
                direction = twin.transform.forward;
            }

            // A camera do Scene View orbita o pivot, entao mirar na parede frontal com um
            // size da ordem da meia altura deixa o observador perto do ponto de olho.
            view.LookAt(target, Quaternion.LookRotation(direction, twin.transform.up),
                Mathf.Max(1f, rig.Height * 0.5f));
        }

        private static void SaveAsPrefab(CaveTwin twin)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Salvar gemeo como prefab", "CaveTwin", "prefab",
                "Escolha onde salvar o prefab do gemeo digital.",
                System.IO.Path.GetDirectoryName(CaveTwinBuilder.PrefabPath).Replace('\\', '/'));

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            GameObject prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(
                twin.gameObject, path, InteractionMode.UserAction);

            if (prefab != null)
            {
                Debug.Log("[CAVE] Gemeo salvo em " + path, prefab);
                EditorGUIUtility.PingObject(prefab);
            }
        }
    }
}
