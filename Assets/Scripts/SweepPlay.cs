using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OneTapDemolition
{
    /// <summary>
    /// 「スイープ解体」のルール。光る帯がビルを上下に往復し、画面のどこかをタップした瞬間に帯がある高さへ一撃を入れる。
    /// ヒビの階(弱点)に当てるとそこから上がまとめて崩れる。真ん中ならPERFECT(倍率×2)。外すとMISSで1発消費。
    /// 連続で当てるほど倍率(コンボ)が上がる。ビルを更地にしたらクリア、弾が尽きたら失敗。
    /// </summary>
    public static class SweepRules
    {
        /// <summary>falseにすると従来の「押して狙う→離して撃つ」ルールに戻る。</summary>
        public const bool Enabled = true;

        /// <summary>帯の中心と弱点の階の中心のずれ(階の高さ比)がこれ以内ならPERFECT。</summary>
        public const float PerfectHalfWidth = 0.22f;
        /// <summary>これ以内なら当たり(GOOD)。階1つ分の幅。</summary>
        public const float HitHalfWidth = 0.5f;
        public const float PerfectBonus = 2f;
        /// <summary>1回当てるごとに帯が速くなる割合。</summary>
        public const float SpeedUpPerHit = 0.1f;

        /// <summary>帯の速さ(1秒に進む階数)。ステージが進むほど速い。</summary>
        public static float Speed(int stageIndex)
        {
            return Mathf.Min(3.2f + 0.15f * (stageIndex - 1), 7.5f);
        }

        /// <summary>combo回目の連続ヒットの倍率(1回目×1、2回目×1.5、3回目×2…)。</summary>
        public static float ComboMultiplier(int combo)
        {
            return 1f + 0.5f * Mathf.Max(0, combo - 1);
        }

        public static StageSpec BuildStage(int stageIndex, float historyBonus)
        {
            System.Random rng = new System.Random(stageIndex * 7919 + 17);
            int n = Mathf.Min(8 + (stageIndex - 1) / 2, 18);
            FloorSpec[] floors = new FloorSpec[n];
            for (int i = 0; i < n; i++)
            {
                floors[i] = FloorSpec.Normal;
            }

            // 弱点の並び(上から順に崩していき、最後は1階=更地)
            List<int> weak = new List<int>();
            int remaining = n;
            while (remaining > 0)
            {
                int w;
                if (remaining <= 3)
                {
                    w = 0;
                }
                else
                {
                    int lo = Mathf.CeilToInt(remaining * 0.3f);
                    int hi = Mathf.FloorToInt(remaining * 0.65f);
                    w = rng.Next(lo, Mathf.Max(lo, hi) + 1);
                }
                weak.Add(w);
                remaining = w;
            }

            int optimal = 0;
            int allGood = 0;
            remaining = n;
            for (int k = 0; k < weak.Count; k++)
            {
                float combo = ComboMultiplier(k + 1);
                for (int step = 1; step <= remaining - weak[k]; step++)
                {
                    optimal += ScoreRules.FloorPoints(step, combo * PerfectBonus, historyBonus, FloorKind.Normal);
                    allGood += ScoreRules.FloorPoints(step, combo, historyBonus, FloorKind.Normal);
                }
                remaining = weak[k];
            }

            return new StageSpec
            {
                StageIndex = stageIndex,
                Floors = floors,
                Shots = weak.Count + (stageIndex <= 10 ? 2 : 1),
                OptimalScore = optimal,
                FirstOptimalTap = -1,
                // ゲージの印: 全部GOODで当てたら1つ目、PERFECTが半分くらいで2つ目
                TargetScore = allGood,
                StarScore = allGood + (optimal - allGood) / 2,
                SweepWeak = weak.ToArray()
            };
        }
    }

    /// <summary>
    /// スイープ解体の進行(帯の往復・タップ判定・弱点の表示)。GameManagerに自動で付く。
    /// </summary>
    public class SweepPlay : MonoBehaviour
    {
        private static readonly Color PerfectColor = new Color(1f, 0.85f, 0.2f, 1f);
        private static readonly Color GoodColor = new Color(0.55f, 0.9f, 1f, 1f);
        private static readonly Color MissColor = new Color(0.75f, 0.75f, 0.8f, 1f);

        private Transform bar;
        private Material barMaterial;
        private BuildingTower tower;
        private int hits;
        private int combo;
        private int misses;
        private float speed;
        private float position;
        private float direction = 1f;
        private int shownWeak = -1;
        private bool cleared;

        /// <summary>帯の今の位置(階の番号の単位。0=1階の中心)。QA用。</summary>
        public float BarPosition => position;
        public int Hits => hits;
        public int Misses => misses;

        private void Start()
        {
            GameManager gm = GameManager.Instance;
            if (gm != null)
            {
                gm.StageStarted += OnStageStarted;
                if (gm.CurrentSpec != null)
                {
                    OnStageStarted(gm.CurrentSpec, gm.ShotsLeft);
                }
            }
        }

        private void OnDestroy()
        {
            if (GameManager.Instance != null)
            {
                GameManager.Instance.StageStarted -= OnStageStarted;
            }
        }

        private void OnStageStarted(StageSpec spec, int shots)
        {
            hits = 0;
            combo = 0;
            misses = 0;
            cleared = false;
            shownWeak = -1;
            speed = SweepRules.Speed(spec.StageIndex);
            position = 0f;
            direction = 1f;
            tower = null;
        }

        /// <summary>今狙うべき弱点の階番号(崩し切ったら-1)。</summary>
        public int CurrentWeak()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.CurrentSpec == null || gm.CurrentSpec.SweepWeak == null || hits >= gm.CurrentSpec.SweepWeak.Length)
            {
                return -1;
            }
            return gm.CurrentSpec.SweepWeak[hits];
        }

        private void Update()
        {
            GameManager gm = GameManager.Instance;
            if (gm == null || gm.CurrentTower == null)
            {
                SetBarVisible(false);
                return;
            }
            if (tower != gm.CurrentTower)
            {
                tower = gm.CurrentTower;
                shownWeak = -1;
            }

            int alive = tower.AliveCount;
            bool active = alive > 0 && gm.State != GameState.Result && !cleared;
            SetBarVisible(active);
            if (!active)
            {
                return;
            }

            ShowWeak(gm.State == GameState.Resolving ? -1 : CurrentWeak());

            // 帯の往復(端でたまらないよう、上下に少しはみ出して折り返す)
            if (gm.State != GameState.Resolving)
            {
                float lo = -0.6f;
                float hi = alive - 1 + 0.6f;
                position += direction * speed * Time.deltaTime;
                if (position > hi)
                {
                    position = hi - (position - hi);
                    direction = -1f;
                }
                else if (position < lo)
                {
                    position = lo + (lo - position);
                    direction = 1f;
                }
            }
            PlaceBar();

            if (gm.CanShoot && PointerDownOutsideUI())
            {
                Strike(gm);
            }
        }

        private static bool PointerDownOutsideUI()
        {
            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                if (t.phase != TouchPhase.Began)
                {
                    return false;
                }
                return EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject(t.fingerId);
            }
            if (!Input.GetMouseButtonDown(0))
            {
                return false;
            }
            return EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject();
        }

        /// <summary>QA用: 帯を指定の位置(階の番号の単位)に置く。</summary>
        public void SetBarPosition(float value)
        {
            position = value;
        }

        /// <summary>
        /// 帯の今の位置で一撃を入れる。QA・録画からも呼べるように公開する。
        /// </summary>
        public void Strike(GameManager gm)
        {
            if (gm == null || !gm.CanShoot)
            {
                return;
            }
            int weak = CurrentWeak();
            if (weak < 0 || tower == null || weak >= tower.AliveCount)
            {
                return;
            }

            float offset = Mathf.Abs(position - weak);
            Floor weakFloor = tower.Floors[weak];
            if (offset <= SweepRules.HitHalfWidth && weakFloor != null)
            {
                bool perfect = offset <= SweepRules.PerfectHalfWidth;
                float multiplier = SweepRules.ComboMultiplier(combo + 1) * (perfect ? SweepRules.PerfectBonus : 1f);
                Vector3 toCamera = Camera.main != null ? (Camera.main.transform.position - weakFloor.transform.position).normalized : Vector3.back;
                if (!tower.RequestDemolish(weakFloor, weakFloor.transform.position + toCamera * 0.5f, multiplier))
                {
                    return;
                }
                combo++;
                hits++;
                speed *= 1f + SweepRules.SpeedUpPerHit;
                string comboText = combo >= 2 ? "  COMBO " + combo : "";
                ScoreFeedbackUI.Instance?.ShowBanner((perfect ? "PERFECT!" : "GOOD") + "  ×" + multiplier.ToString("0.#") + comboText,
                    perfect ? PerfectColor : GoodColor);
                if (perfect)
                {
                    JuiceManager.Instance?.PlayStarNote(Mathf.Min(2, combo - 1));
                }
                ShowWeak(-1);
                if (hits >= gm.CurrentSpec.SweepWeak.Length)
                {
                    cleared = true;
                    if (misses == 0)
                    {
                        gm.TryAwardStar(StarReason.BonusFloor, weakFloor.transform.position);
                    }
                }
                return;
            }

            // 外した: 帯の下の階がガキンと揺れるだけで崩れない。コンボは途切れる
            if (!gm.TryConsumeShot())
            {
                return;
            }
            misses++;
            combo = 0;
            int under = Mathf.Clamp(Mathf.RoundToInt(position), 0, tower.AliveCount - 1);
            tower.Floors[under]?.StartCrack(0.18f);
            JuiceManager.Instance?.PlayFail();
            ScoreFeedbackUI.Instance?.ShowBanner("MISS", MissColor);
            StartCoroutine(FinishMiss(gm));
        }

        private IEnumerator FinishMiss(GameManager gm)
        {
            yield return new WaitForSeconds(0.35f);
            gm.OnChainFinished();
        }

        private void ShowWeak(int index)
        {
            if (index == shownWeak || tower == null)
            {
                return;
            }
            shownWeak = index;
            for (int i = 0; i < tower.Floors.Count; i++)
            {
                Floor f = tower.Floors[i];
                if (f != null && !f.IsDemolished)
                {
                    f.SetAim(i == index ? AimState.Weak : AimState.None);
                }
            }
        }

        /// <summary>帯の位置(階の番号の単位)から、ワールド座標に置く。</summary>
        private void PlaceBar()
        {
            EnsureBar();
            Floor f0 = tower.Floors.Count > 0 ? tower.Floors[0] : null;
            Renderer r0 = f0 != null ? f0.GetComponent<Renderer>() : null;
            if (r0 == null)
            {
                return;
            }

            // 1階の見た目の中心を基準に、階の間隔ずつ上へ(1階が崩れて動いても基準はビルの位置から取る)
            Bounds b0 = r0.bounds;
            float step = tower.FloorHeight * tower.transform.lossyScale.y;
            float centerOffset = f0.IsDemolished ? step * 0.5f : b0.center.y - (tower.transform.position.y);
            float y = tower.transform.position.y + centerOffset + position * step;
            Vector3 c0 = tower.transform.position;
            bar.position = new Vector3(c0.x, y, c0.z);
            bar.localScale = new Vector3(b0.size.x * 1.3f, step * 0.2f, b0.size.z * 1.3f);

            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 14f);
            // ビルの黄色い装飾と見分けやすいよう水色に光らせる
            Color c = Color.Lerp(new Color(0.2f, 1f, 1f, 1f), Color.white, pulse * 0.5f);
            barMaterial.color = c;
            if (barMaterial.HasProperty("_BaseColor"))
            {
                barMaterial.SetColor("_BaseColor", c);
            }
        }

        private void EnsureBar()
        {
            if (bar != null)
            {
                return;
            }
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "SweepBar";
            Destroy(go.GetComponent<Collider>());
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }
            Renderer renderer = go.GetComponent<Renderer>();
            if (shader != null)
            {
                barMaterial = new Material(shader);
                renderer.sharedMaterial = barMaterial;
            }
            else
            {
                barMaterial = renderer.material;
            }
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bar = go.transform;
        }

        private void SetBarVisible(bool visible)
        {
            if (bar != null && bar.gameObject.activeSelf != visible)
            {
                bar.gameObject.SetActive(visible);
            }
        }
    }
}
