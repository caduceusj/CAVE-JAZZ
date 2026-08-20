using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace CaveJazz.Calibration.EditorTools
{
    /// <summary>
    /// Constroi o gemeo digital da CAVE a partir de um <see cref="CaveRig"/>.
    ///
    /// Gera GameObjects normais, salvos na cena, e materiais como asset (material
    /// embutido em cena nao sobrevive ao virar prefab). Regerar localiza as pecas
    /// pelo nome e reescreve so transform e material: cor ajustada a mao e filhos
    /// acrescentados por fora continuam onde estao.
    /// </summary>
    public static class CaveTwinBuilder
    {
        public const string MaterialsFolder = "Assets/CavePackage/Materials/Twin";
        public const string PrefabPath = "Assets/CavePackage/CaveTwin.prefab";

        private const string UndoLabel = "Gerar gemeo digital da CAVE";
        private const string TagManagerPath = "ProjectSettings/TagManager.asset";

        /// <summary>Primeira camada de usuario. De 0 a 7 sao as builtin do Unity.</summary>
        private const int FirstUserLayer = 8;

        /// <summary>
        /// Folga entre a superficie de projecao e o casco atras dela. Sem ela as duas faces
        /// ficam coplanares e brigam por z-fighting.
        /// </summary>
        private const float SurfaceGap = 0.01f;

        private static readonly Dictionary<PrimitiveType, Mesh> BuiltinMeshes =
            new Dictionary<PrimitiveType, Mesh>();

        // ================================================================== construcao

        public static bool Build(CaveTwin twin, bool askConfirmation = true)
        {
            if (twin == null)
            {
                return false;
            }

            CaveRig rig = twin.ResolveSource();
            if (rig == null)
            {
                EditorUtility.DisplayDialog("Gemeo digital da CAVE",
                    "Nenhum CaveRig encontrado. Adicione o componente Cave Rig ao CaveRoot antes de gerar o gemeo.",
                    "Fechar");
                return false;
            }

            rig.EnsureBindings();

            int layer = ResolveLayer(twin.twinLayerName, out bool layerCreated);
            List<Camera> maskChanges = CollectMaskChanges(twin, rig, layer);

            if (askConfirmation
                && !EditorUtility.DisplayDialog("Gerar gemeo digital da CAVE",
                    BuildSummary(twin, rig, layer, layerCreated, maskChanges), "Gerar", "Cancelar"))
            {
                return false;
            }

            Undo.SetCurrentGroupName(UndoLabel);
            int undoGroup = Undo.GetCurrentGroup();

            Undo.RecordObject(twin.transform, UndoLabel);
            twin.ApplyPlacement();

            BuildScreens(twin, rig, EnsureGroup(twin.transform, CaveTwin.ScreensGroup));
            BuildStructure(twin, rig, EnsureGroup(twin.transform, CaveTwin.StructureGroup));
            BuildReferences(twin, rig, EnsureGroup(twin.transform, CaveTwin.ReferencesGroup), layer);

            if (layer >= 0)
            {
                SetLayerRecursively(twin.transform, layer);
                ApplyMaskChanges(maskChanges, layer);
            }

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();

            Debug.Log("[CAVE] Gemeo digital gerado a partir de " + rig.name + ".", twin);
            return true;
        }

        public static void Remove(CaveTwin twin)
        {
            if (twin == null)
            {
                return;
            }

            Undo.SetCurrentGroupName("Remover gemeo digital da CAVE");
            int undoGroup = Undo.GetCurrentGroup();

            string[] groups = { CaveTwin.ScreensGroup, CaveTwin.StructureGroup, CaveTwin.ReferencesGroup };
            for (int i = 0; i < groups.Length; i++)
            {
                Transform child = twin.transform.Find(groups[i]);
                if (child != null)
                {
                    Undo.DestroyObjectImmediate(child.gameObject);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
        }

        // ================================================================== telas

        private static void BuildScreens(CaveTwin twin, CaveRig rig, Transform parent)
        {
            Shader unlit = FindShader("Universal Render Pipeline/Unlit", "Unlit/Texture");
            Mesh quad = GetBuiltinMesh(PrimitiveType.Quad);
            Transform rigTransform = rig.transform;

            for (int i = 0; i < CaveRig.FaceCount; i++)
            {
                CaveFace face = (CaveFace)i;
                RenderTexture renderTexture = rig.GetRenderTexture(face);

                Material material = EnsureMaterial("Twin_Tela_" + face, unlit, Color.white, renderTexture);
                Transform screen = EnsurePiece(parent, "Tela_" + face, quad, material);

                Undo.RecordObject(screen, UndoLabel);

                Camera camera = rig.GetCamera(face);
                if (camera != null && rig.TryGetFaceBoundsInCameraSpace(face, camera, out CaveFaceBounds bounds))
                {
                    // O Quad do Unity tem normal -Z e UV (0,0) no canto inferior esquerdo.
                    // Copiar a rotacao da camera deixa a face virada para o olho e a imagem
                    // no mesmo sentido que o projetor recebe, inclusive o giro de 180 graus
                    // da CamTraseira.
                    Vector3 centerWorld = camera.transform.TransformPoint(new Vector3(
                        (bounds.left + bounds.right) * 0.5f,
                        (bounds.bottom + bounds.top) * 0.5f,
                        bounds.distance));

                    screen.localPosition = rigTransform.InverseTransformPoint(centerWorld);
                    screen.localRotation = Quaternion.Inverse(rigTransform.rotation) * camera.transform.rotation;
                    screen.localScale = new Vector3(bounds.Width, bounds.Height, 1f);
                }
                else
                {
                    CaveFaceRect rect = rig.GetFaceRect(face);
                    screen.localPosition = rect.center;
                    screen.localRotation = Quaternion.LookRotation(-rect.normal, rect.up);
                    screen.localScale = new Vector3(rect.size.x, rect.size.y, 1f);
                }
            }
        }

        // ================================================================== estrutura

        private static void BuildStructure(CaveTwin twin, CaveRig rig, Transform parent)
        {
            float w = rig.Width;
            float h = rig.Height;
            float d = rig.Depth;
            float y0 = rig.floorLocalY;

            float t = Mathf.Max(0.01f, twin.structureThickness);
            float e = Mathf.Max(0.01f, twin.edgeThickness);

            float midY = y0 + h * 0.5f;
            float topY = y0 + h + e * 0.5f;

            // O casco comeca uma folga atras da superficie de projecao.
            float shellOffset = SurfaceGap + t * 0.5f;
            float wallX = w * 0.5f + shellOffset;
            float wallZ = d * 0.5f + shellOffset;
            float outerW = w + 2f * (SurfaceGap + t);
            float outerD = d + 2f * (SurfaceGap + t);

            // Pilares inteiramente por fora da sala, para nao cobrirem o canto das telas.
            float postX = w * 0.5f + SurfaceGap + (t + e) * 0.5f;
            float postZ = d * 0.5f + SurfaceGap + (t + e) * 0.5f;

            Shader lit = FindShader("Universal Render Pipeline/Lit", "Standard");
            Material shell = EnsureMaterial("Twin_Casco", lit, new Color(0.16f, 0.16f, 0.18f), null);
            Material edge = EnsureMaterial("Twin_Quina", lit, new Color(0.34f, 0.35f, 0.38f), null);

            // Casco: piso e as quatro paredes, logo atras de cada tela. As paredes se
            // interpenetram nos cantos, o que e invisivel entre solidos opacos.
            Cube(twin, parent, "Casco_Piso", new Vector3(0f, y0 - shellOffset, 0f),
                new Vector3(outerW, t, outerD), shell);
            Cube(twin, parent, "Casco_Frontal", new Vector3(0f, midY, wallZ),
                new Vector3(outerW, h, t), shell);
            Cube(twin, parent, "Casco_Traseira", new Vector3(0f, midY, -wallZ),
                new Vector3(outerW, h, t), shell);
            Cube(twin, parent, "Casco_Direita", new Vector3(wallX, midY, 0f),
                new Vector3(t, h, outerD), shell);
            Cube(twin, parent, "Casco_Esquerda", new Vector3(-wallX, midY, 0f),
                new Vector3(t, h, outerD), shell);

            Vector3 postScale = new Vector3(t + e, h, t + e);
            Cube(twin, parent, "Quina_FrenteDireita", new Vector3(postX, midY, postZ), postScale, edge);
            Cube(twin, parent, "Quina_FrenteEsquerda", new Vector3(-postX, midY, postZ), postScale, edge);
            Cube(twin, parent, "Quina_TrasDireita", new Vector3(postX, midY, -postZ), postScale, edge);
            Cube(twin, parent, "Quina_TrasEsquerda", new Vector3(-postX, midY, -postZ), postScale, edge);

            // Vigas em volta da abertura superior: e o que faz a sala ler como aberta em cima,
            // que e como a planificacao do CanvasAligned descreve a CAVE real.
            Vector3 railX = new Vector3(outerW + 2f * (t + e), e, t + e);
            Vector3 railZ = new Vector3(t + e, e, outerD);
            Cube(twin, parent, "Borda_Frontal", new Vector3(0f, topY, wallZ), railX, edge);
            Cube(twin, parent, "Borda_Traseira", new Vector3(0f, topY, -wallZ), railX, edge);
            Cube(twin, parent, "Borda_Direita", new Vector3(wallX, topY, 0f), railZ, edge);
            Cube(twin, parent, "Borda_Esquerda", new Vector3(-wallX, topY, 0f), railZ, edge);
        }

        private static void Cube(CaveTwin twin, Transform parent, string name,
            Vector3 position, Vector3 scale, Material material)
        {
            Transform piece = EnsurePiece(parent, name, GetBuiltinMesh(PrimitiveType.Cube), material);

            Undo.RecordObject(piece, UndoLabel);
            piece.localPosition = position;
            piece.localRotation = Quaternion.identity;
            piece.localScale = scale;

            BoxCollider collider = piece.GetComponent<BoxCollider>();
            if (twin.generateColliders && collider == null)
            {
                Undo.AddComponent<BoxCollider>(piece.gameObject);
            }
            else if (!twin.generateColliders && collider != null)
            {
                Undo.DestroyObjectImmediate(collider);
            }
        }

        // ================================================================== referencias

        private static void BuildReferences(CaveTwin twin, CaveRig rig, Transform parent, int layer)
        {
            Vector3 eye = rig.EyeLocalPosition;
            float y0 = rig.floorLocalY;

            Shader lit = FindShader("Universal Render Pipeline/Lit", "Standard");
            Material reference = EnsureMaterial("Twin_Referencia", lit, new Color(1f, 0.78f, 0.25f), null);

            Transform marker = EnsurePiece(parent, CaveTwin.EyeMarkerName,
                GetBuiltinMesh(PrimitiveType.Sphere), reference);
            Undo.RecordObject(marker, UndoLabel);
            marker.localPosition = eye;
            marker.localRotation = Quaternion.identity;
            marker.localScale = Vector3.one * Mathf.Max(0.05f, rig.Width * 0.015f);

            BuildHumanReference(twin, parent, eye, y0, reference);
            BuildObserver(twin, rig, parent, eye, layer);
        }

        private static void BuildHumanReference(CaveTwin twin, Transform parent,
            Vector3 eye, float floorY, Material material)
        {
            float height = twin.HumanHeightInUnits;
            bool custom = twin.humanReferenceMesh != null;
            Mesh mesh = custom ? twin.humanReferenceMesh : GetBuiltinMesh(PrimitiveType.Capsule);

            Transform human = EnsurePiece(parent, CaveTwin.HumanReferenceName, mesh, material);
            Undo.RecordObject(human, UndoLabel);
            human.localRotation = Quaternion.identity;

            if (custom)
            {
                // Reescala pelos bounds da mesh e apoia a base no piso.
                Bounds bounds = mesh.bounds;
                float scale = bounds.size.y > 0.0001f ? height / bounds.size.y : 1f;
                human.localScale = Vector3.one * scale;
                human.localPosition = new Vector3(eye.x, floorY - bounds.min.y * scale, eye.z);
            }
            else
            {
                // A capsula do Unity tem 2 unidades de altura e 1 de diametro.
                float shoulders = Mathf.Max(0.05f, height * 0.26f);
                human.localScale = new Vector3(shoulders, height * 0.5f, shoulders);
                human.localPosition = new Vector3(eye.x, floorY + height * 0.5f, eye.z);
            }
        }

        private static void BuildObserver(CaveTwin twin, CaveRig rig, Transform parent, Vector3 eye, int layer)
        {
            Transform observer = EnsureEmpty(parent, CaveTwin.ObserverName);

            Undo.RecordObject(observer, UndoLabel);
            observer.localPosition = eye;
            observer.localRotation = Quaternion.identity;
            observer.localScale = Vector3.one;

            Camera camera = observer.GetComponent<Camera>();
            if (camera == null)
            {
                camera = Undo.AddComponent<Camera>(observer.gameObject);
            }

            Undo.RecordObject(camera, UndoLabel);

            // Desabilitada de proposito: uma camera ativa aqui disputaria com o rig da CAVE.
            // Quem quiser o ponto de vista do visitante habilita na mao.
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = Mathf.Max(100f, (rig.Width + rig.Depth) * 4f);
            camera.fieldOfView = 60f;

            if (layer >= 0)
            {
                camera.cullingMask = 1 << layer;
            }
        }

        // ================================================================== pecas

        private static Transform EnsureGroup(Transform parent, string name)
        {
            Transform group = EnsureEmpty(parent, name);

            Undo.RecordObject(group, UndoLabel);
            group.localPosition = Vector3.zero;
            group.localRotation = Quaternion.identity;
            group.localScale = Vector3.one;

            return group;
        }

        private static Transform EnsureEmpty(Transform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                return existing;
            }

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            Undo.RegisterCreatedObjectUndo(go, UndoLabel);
            return go.transform;
        }

        private static Transform EnsurePiece(Transform parent, string name, Mesh mesh, Material material)
        {
            Transform piece = EnsureEmpty(parent, name);
            GameObject go = piece.gameObject;

            MeshFilter filter = go.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = Undo.AddComponent<MeshFilter>(go);
            }

            if (mesh != null && filter.sharedMesh != mesh)
            {
                Undo.RecordObject(filter, UndoLabel);
                filter.sharedMesh = mesh;
            }

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = Undo.AddComponent<MeshRenderer>(go);
            }

            if (material != null && renderer.sharedMaterial != material)
            {
                Undo.RecordObject(renderer, UndoLabel);
                renderer.sharedMaterial = material;
            }

            return piece;
        }

        /// <summary>
        /// Mesh builtin do Unity, pega de uma primitiva descartavel. A mesh e um asset
        /// interno e sobrevive a destruicao do GameObject temporario.
        /// </summary>
        private static Mesh GetBuiltinMesh(PrimitiveType type)
        {
            if (BuiltinMeshes.TryGetValue(type, out Mesh cached) && cached != null)
            {
                return cached;
            }

            GameObject temp = GameObject.CreatePrimitive(type);
            Mesh mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(temp);

            BuiltinMeshes[type] = mesh;
            return mesh;
        }

        // ================================================================== materiais

        private static Material EnsureMaterial(string name, Shader shader, Color color, Texture texture)
        {
            EnsureFolder(MaterialsFolder);

            string path = MaterialsFolder + "/" + name + ".mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (material == null)
            {
                material = new Material(shader) { name = name };
                SetColor(material, color);
                AssetDatabase.CreateAsset(material, path);
            }

            // A cor so e escrita na criacao, para o ajuste manual sobreviver a um regerar.
            // A Render Texture e sempre reapontada, porque e ela que liga o gemeo ao rig.
            if (texture != null)
            {
                SetTexture(material, texture);
                EditorUtility.SetDirty(material);
            }

            return material;
        }

        private static void SetColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        private static void SetTexture(Material material, Texture texture)
        {
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }

            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", texture);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "Assets" || AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(parent))
            {
                return;
            }

            parent = parent.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static Shader FindShader(string primary, string fallback)
        {
            Shader shader = Shader.Find(primary);
            if (shader == null)
            {
                shader = Shader.Find(fallback);
            }

            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            return shader;
        }

        // ================================================================== camada

        private static int ResolveLayer(string name, out bool created)
        {
            created = false;

            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            int existing = LayerMask.NameToLayer(name);
            if (existing >= 0)
            {
                return existing;
            }

            int layer = CreateLayer(name);
            created = layer >= 0;
            return layer;
        }

        private static int CreateLayer(string name)
        {
            Object[] assets = AssetDatabase.LoadAllAssetsAtPath(TagManagerPath);
            if (assets == null || assets.Length == 0)
            {
                return -1;
            }

            SerializedObject tagManager = new SerializedObject(assets[0]);
            SerializedProperty layers = tagManager.FindProperty("layers");
            if (layers == null)
            {
                return -1;
            }

            for (int i = FirstUserLayer; i < layers.arraySize; i++)
            {
                SerializedProperty element = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(element.stringValue))
                {
                    element.stringValue = name;
                    tagManager.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                    return i;
                }
            }

            Debug.LogWarning("[CAVE] Nao ha camada de usuario livre para o gemeo digital. " +
                             "Libere uma camada entre 8 e 31 e gere de novo.");
            return -1;
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root.gameObject.layer != layer)
            {
                Undo.RecordObject(root.gameObject, UndoLabel);
                root.gameObject.layer = layer;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }

        private static List<Camera> CollectMaskChanges(CaveTwin twin, CaveRig rig, int layer)
        {
            List<Camera> changes = new List<Camera>();

            if (!twin.excludeFromRigCameras || layer < 0)
            {
                return changes;
            }

            int bit = 1 << layer;
            Camera[] cameras = rig.GetRigCameras();

            for (int i = 0; i < cameras.Length; i++)
            {
                if ((cameras[i].cullingMask & bit) != 0)
                {
                    changes.Add(cameras[i]);
                }
            }

            return changes;
        }

        private static void ApplyMaskChanges(List<Camera> cameras, int layer)
        {
            int bit = 1 << layer;

            for (int i = 0; i < cameras.Count; i++)
            {
                Camera camera = cameras[i];
                if (camera == null)
                {
                    continue;
                }

                Undo.RecordObject(camera, UndoLabel);
                camera.cullingMask &= ~bit;
                EditorUtility.SetDirty(camera);

                if (PrefabUtility.IsPartOfPrefabInstance(camera))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(camera);
                }
            }
        }

        // ================================================================== resumo

        private static string BuildSummary(CaveTwin twin, CaveRig rig, int layer,
            bool layerCreated, List<Camera> maskChanges)
        {
            Vector3 dimensions = rig.Dimensions;

            string summary = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "Sala: {0:F3} x {1:F3} x {2:F3} u (aberta em cima)\nRig: {3}\n\n",
                dimensions.x, dimensions.y, dimensions.z, rig.name);

            summary += "Serao criadas ou atualizadas as 5 telas, o casco, os pilares, "
                       + "as vigas do topo e as referencias de escala.\n\n";

            if (layer < 0)
            {
                summary += "Sem camada dedicada: o gemeo pode aparecer dentro do render da CAVE.\n";
            }
            else
            {
                summary += "Camada \"" + twin.twinLayerName + "\" (indice " + layer + ")"
                           + (layerCreated ? " - sera criada agora.\n" : " - ja existe.\n");

                if (maskChanges.Count > 0)
                {
                    summary += "Sera removida do culling mask de " + maskChanges.Count
                               + " camera(s) do rig, para a maquete nao aparecer dentro das "
                               + "Render Textures.\n";
                }
            }

            summary += "\nCor de material ajustada a mao e filhos acrescentados por fora "
                       + "sao preservados. Tudo e desfeito com Ctrl+Z.";

            return summary;
        }
    }
}
