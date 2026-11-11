# CAVE Package — projeção, calibração e imagem

Pacote da CAVE de 5 superfícies (4 paredes + piso) que sai pelo Spout num atlas de
**3270 × 4500** no formato do arquivo `Prefabs/CAVE/UV Map - Sala Imersiva (1).jpg`.

| Arquivo | Para quê |
| --- | --- |
| `Scripts/CaveRig.cs` | Rig novo: projeção off-axis, piso cortado no chão, composição pixel a pixel do atlas |
| `Scripts/CaveSurface.cs` | Geometria da sala, orientação de cada superfície no atlas, matriz de projeção |
| `Scripts/CaveCalibrationGrid.cs` | Grade de calibração (sobre as superfícies e "além" das paredes) |
| `Scripts/CaveDayNightLighting.cs` | Dia/noite "estilo Hollywood" (noite azulada, mas clara) |
| `CaveRoot.prefab` | Câmeras + `CaveRig` já configurado |
| `CanvasAligned.prefab` | Compositor antigo (canvas). Fica desligado enquanto o `CaveRig` roda |

---

## 1. O que estava causando cada problema

### Imagem embaçada / pouca nitidez

1. **Meio pixel de deslocamento no canvas.** Piso, parede esquerda e parede direita têm 2625 px
   (ímpar) e eram centralizados num pixel inteiro do `CanvasAligned`. As bordas caíam em x,5 e cada
   pixel de saída virava a média 50/50 de dois texels (filtro bilinear). É um desfoque de meio pixel
   num eixo — em ~76% da área projetada.
2. **Regiões sobrepostas no canvas.** O piso (desenhado por último) cobria 6 px das laterais e
   3,5 px da frontal/traseira: cada parede perdia a faixa de baixo e a costura com o piso desalinhava.
3. **Render textures com 8 bits lineares** (`R8G8B8A8_UNorm`): sombras e gradientes escuros viram
   degraus (banding). Agora são `R8G8B8A8_SRGB`.
4. **Sem anti-aliasing** nas câmeras da CAVE (serrilhado/cintilação). Agora: SMAA High.
5. **Texturas 4K importadas a 2048 px**: a "Textura Melhorada 4K" da Ella (4096²), a do Louis
   (4096²), o veludo (4096×3941), o vinil (8504²) e a parede de autógrafos (5120²). Agora até 4096
   (avatares e autógrafos com compressão de alta qualidade).
6. **`SpoutSender` sem configuração.** No commit `0e67da9` ele perdeu todos os campos (fonte,
   modo de captura). Sem eles o padrão é capturar a *Game View*. Restaurado: modo *Texture* → `RT_Send`.

### Piso distorcido

A câmera do piso estava **1,11 m acima do piso do clube** (y 4,01 vs piso em y 2,9) com lente de
112° × 141°, e todas as câmeras usavam frustum simétrico centrado — isso só é correto para um olho
no centro da sala **à metade da altura das paredes**. Para um adulto (~1,60 m) a perspectiva do
chão fica errada, e o chão virtual sai ampliado (com uma sala de 4,5 × 8,4 m, ~1,36×).

A lente aberta em si não é o problema: o piso é grande e está perto do olho, então o frustum
correto é largo mesmo. O problema é **o centro de projeção não estar nos olhos do público**.
A solução proposta pela equipe é exatamente o que o rig faz:

* câmera do piso na **altura dos olhos** apontando para baixo;
* **near plane colado no chão** (`floor.nearMode = ClipAtSurface`, `clipOffset` = 5 cm):
  tudo que estiver entre o público e o piso (mesa, peixe, água…) é cortado da projeção do piso;
* frustum **off-axis** (Kooima, *Generalized Perspective Projection*) passando exatamente pelas
  bordas físicas do piso.

### Quinas ("o peixe curva quando passa de uma parede para a outra")

Três causas, das mais fáceis às inevitáveis:

1. **Erros do setup antigo, visíveis até do ponto ideal:** FOV das laterais arredondado
   (68,23° em vez do valor exato) → até ~9,5 px de desencontro nas pontas das laterais; e as
   sobreposições do canvas acima. **Corrigido** — o rig calcula o frustum das medidas e o teste
   numérico dá erro zero nas quinas a partir do olho.
2. **Altura do olho errada (a maior causa para o público):** uma linha reta que atravessa a quina
   parece dobrar **~2,9° a cada 10 cm** de diferença entre a altura dos olhos de quem vê e a altura
   usada na renderização (simulação com sala 4,5 × 8,4 × 3 m):

   | olhos de quem vê (render a 1,60 m) | 1,10 | 1,30 | 1,45 | 1,55 | **1,60** | 1,75 | 1,90 |
   | --- | --- | --- | --- | --- | --- | --- | --- |
   | dobra na quina | 14,2° | 8,6° | 4,3° | 1,4° | **0°** | 4,3° | 8,6° |

   O setup antigo renderizava para olhos a meia altura da parede. **Ajuste `eyePosition.y` para a
   altura de olhos do público** (≈1,60 m em pé; ≈1,20 m sentado/crianças).
3. **Paralaxe para quem está fora do sweet spot (inevitável sem rastreamento):** andar 1 m para o
   lado gera ~2° de dobra com o peixe 0,8 m acima da linha dos olhos, e quase zero com o peixe na
   linha dos olhos ou longe das paredes. Na prática:
   * faça os peixes atravessarem as quinas **perto da linha dos olhos** e/ou mais longe;
   * evite cruzar quinas rente ao chão ou bem no alto, perto do público;
   * marque no chão a área boa para o público ficar (o centro);
   * para uma pessoa só, dá para eliminar com rastreamento de cabeça (`trackedEye`).

   Ative `depthGrid` no `CaveCalibrationGrid` para ver esse efeito ao vivo.

**Volume da sala vazio no mundo virtual:** objetos que caem *dentro* da sala física (entre o público
e as paredes) não têm como aparecer certos numa CAVE mono — aparecem "pintados" na parede e o piso
os corta. Posicione o `CaveRoot` num espaço livre do cenário (a caixa branca do gizmo mostra o
volume). Com a sala-exemplo de 4,5 × 8,4 m, hoje ficam dentro dela os *Table Set (7)* e *(9)*
(mesa, poltrona e charutos) e duas *High stool* do balcão.

### Tudo muito escuro

1. **Tonemapping ACES aplicado duas vezes**, e sobre buffer LDR: cada câmera da CAVE aplicava
   ACES (com HDR desligado, ou seja, tudo acima de 1 já estourado) e a `CamSender` aplicava **de
   novo** sobre o atlas pronto (mais vinheta e bloom). Resultado medido com a curva ACES do URP:

   | luz na cena (linear) | antes (2× ACES, LDR) | depois (1× ACES, HDR) | depois + 0,5 EV (aplicado) |
   | --- | --- | --- | --- |
   | 0,05 (sombra) | 11/255 | 33/255 | 45/255 |
   | 0,18 (cinza médio) | **63/255** | 92/255 | **116/255** |
   | 1,0 (branco) | 182/255 | 208/255 | 221/255 |
   | 4,0 (lâmpada) | **182/255** (teto) | 241/255 | 244/255 |

   Antes, **nenhum pixel da CAVE passava de 182/255** (71% de cinza).
2. **Vinheta em cada parede**: escurecia as bordas de cada superfície, ou seja, justamente as
   quinas. Desligada no `SampleSceneProfile`.
3. **Spots principais não iluminavam nada**: "Singer" (intensidade 51,45) e "Sofa" (12,55) estavam
   em modo *Baked*, mas nenhum objeto da cena é estático e não há lightmaps — luz Baked sem
   lightmap não ilumina em runtime. Agora estão em *Realtime*.
4. **HDR desligado** nas câmeras da CAVE: luzes fortes estouravam antes do tonemapping. Ligado.
5. **SSAO** (renderer `PC_Renderer`): escurece e, por ser calculado por câmera, cria uma costura de
   brilho nas quinas. Desligado. (Se quiser AO, prefira AO assado nas texturas/lightmaps.)
6. **Exposição**: `Color Adjustments → Post Exposure = +0,5 EV` no `SampleSceneProfile`
   (cinza médio 63 → 116/255). Ajuste a gosto — é o botão certo para "mais luz nos olhos".

---

## 2. Como usar o `CaveRig`

O `CaveRig` fica no `CaveRoot`. **O transform do `CaveRoot` é o centro do piso físico** dentro do
mundo virtual; o eixo +Z aponta para a parede frontal. Na `BaseScene` ele está em
`(1,435; 2,9; -2,091)`: mesma posição horizontal que a equipe já usava, agora **no piso do clube**
(topo dos cubos `Flor1/Flor2`).

1. **Meça a sala real e preencha** `roomWidth` (esquerda↔direita), `roomDepth` (traseira↔frontal)
   e `roomHeight` (do piso até a borda de cima da imagem nas paredes), em metros.
   Os valores atuais (4,50 × 8,40 × 3,00) são **estimativas** a partir das proporções do mapa UV.
   Se só der para medir a altura: menu de contexto do componente →
   *Medidas/Estimar largura e profundidade pela altura + mapa UV*.
2. `eyePosition`: posição dos olhos do público em metros, relativa ao centro do piso
   (padrão `0; 1,6; 0`).
3. `worldUnitsPerMeter`: 1 = escala real (1 unidade = 1 m, como os avatares).
4. **Layout do atlas**: o padrão segue as linhas do mapa UV da sala
   (traseira 1406×933, piso 1406×2626, frontal 1406×941, esquerda 934×2626, direita 930×2626).
   Se o media server foi calibrado em cima da saída antiga, use o menu de contexto
   *Layout/Igual ao CanvasAligned antigo*. As regiões são editáveis por superfície.
5. Dê **Play**. O rig desliga a `CamSender` e o `CanvasAligned` e escreve direto na `RT_Send`
   (que o `SpoutSender` envia). Desligando o componente, tudo volta ao pipeline antigo.
6. `previewInEditMode` aplica o rig fora do Play Mode para calibrar; o estado original das câmeras
   é restaurado antes de salvar a cena e antes de entrar no Play Mode.

O rig controla **posição, rotação, projeção, near/far e render target** das 5 câmeras. O resto
(culling mask, pós-processamento, HDR, AA, renderer) continua configurável em cada câmera.

### Calibração na sala

1. Ative o `CaveCalibrationGrid` (já está no `CaveRoot`, desligado).
2. **Grade de superfície** (`surfaceGrid`): linhas de 50 cm desenhadas *sobre* as paredes e o piso.
   Ela independe de onde você está. As bordas brancas têm que cair nas quinas reais e as linhas
   têm que continuar retas de uma superfície para a outra. Se não cair: confira as medidas
   (`room*`) e o mapeamento das regiões no media server — não mexa no `eyePosition` por isso.
3. **Grade de profundidade** (`depthGrid`): caixa 2 m além das paredes. Do ponto `eyePosition` as
   linhas cruzam as quinas retas; agachando ou andando elas dobram. Use para escolher a altura dos
   olhos e a área do público.

---

## 3. Dia e noite com "luz nos olhos"

A noite de cinema não é escura: é **azulada**, com uma luz principal forte ("lua"), contraluz e
preenchimento suficiente para ver rostos. Muda a cor e a direção da luz, não a quantidade. Numa
CAVE isso pesa mais ainda: o preto de uma parede é lavado pela luz das outras paredes, então cena
escura vira cinza lamacento — mantenha a imagem nos **tons médios** e use **cor** para dizer
"noite".

`CaveDayNightLighting` faz isso: interpola um preset de dia e um de noite (luz direcional sol/lua,
ambiente em gradiente céu/horizonte/chão, neblina e, opcionalmente, o peso de dois `Volume`s globais
— por exemplo um de noite com *White Balance* mais frio e *Post Exposure* +0,5). O controle
`brightness` é o "quanta luz entra no olho do público". Regras práticas:

* cinza médio da imagem entre ~100 e 140/255 tanto de dia quanto de noite;
* noite = temperatura mais fria e um pouco menos de saturação, **não** menos exposição;
* sempre uma luz principal + um preenchimento; nunca ambiente preto;
* evite grandes áreas pretas na projeção (a sala as transforma em cinza).

---

## 4. Checklist para testar no Unity

- [ ] Abrir a `BaseScene`, selecionar o `CaveRoot` e ver o gizmo (caixa branca = sala, esfera
      amarela = olhos, linhas coloridas = frustums, retângulo ciano = corte do piso).
- [ ] Preencher as medidas reais da sala.
- [ ] Play → `RT_Send` deve mostrar a cruz (frontal em cima, traseira de cabeça para baixo,
      esquerda girada 90° anti-horário, direita 90° horário), com cantos pretos.
- [ ] Ligar o `CaveCalibrationGrid` e conferir as quinas na sala real.
- [ ] Conferir no receptor do Spout que a imagem chega com 3270 × 4500.
- [ ] Ajustar `Post Exposure` no `SampleSceneProfile` olhando a projeção na sala.

## 5. Notas técnicas

* Cada câmera renderiza numa textura com o **tamanho exato** da sua região do atlas (formato igual
  ao da `RT_Send`) e o rig copia com `Graphics.CopyTexture` no fim do frame: cópia byte a byte, sem
  filtro. O pós-processamento acontece uma vez só, em cada câmera.
* A orientação de cada superfície no atlas é feita girando a câmera (o "cima" da imagem), não a
  imagem: frontal (cima = teto), traseira (cima = piso), esquerda/direita (cima = frontal),
  piso (cima = frontal). Validado numericamente contra o pipeline antigo (mesma orientação nas 5
  faces) e contra a interseção física raio/parede (erro < 1e-9 px).
* Ainda **não testado dentro do Unity** por quem escreveu: o código foi compilado contra as DLLs de
  referência do Unity (player e editor) e a matemática foi validada fora dele. Primeiro Play deve
  ser feito com o checklist acima.
