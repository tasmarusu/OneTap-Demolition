using System.Collections.Generic;
using UnityEngine;

namespace OneTapDemolition
{
    public enum FloorKind
    {
        Normal,
        Gate,
        Bonus
    }

    /// <summary>
    /// 1階分の種類。Gateは連鎖でその階以降に掛かる倍率、
    /// Bonusは得点が2倍で、壊すと☆が1つ出る特別な階(金色に脈動する)。
    /// ゲートには、時間で倍率が切り替わる「ライブゲート」(Cycle)と、常に同じ倍率の固定ゲートがある。
    /// ライブゲートは、指を離した瞬間(=タップが確定した瞬間)の倍率で、その一撃の連鎖が確定する。
    /// </summary>
    public struct FloorSpec
    {
        public FloorKind Kind;
        /// <summary>固定ゲートの倍率。ライブゲートでは周期の最大値(色分けの目安)。</summary>
        public float GateValue;
        /// <summary>ライブゲートの倍率の並び(各値は同じ長さずつ続く)。固定ゲート/通常階はnull。</summary>
        public float[] Cycle;
        public float Period;
        public float Phase;

        public bool IsLive => Kind == FloorKind.Gate && Cycle != null && Cycle.Length > 1;

        public static FloorSpec Normal => new FloorSpec { Kind = FloorKind.Normal, GateValue = 1f };
        public static FloorSpec Gate(float value) => new FloorSpec { Kind = FloorKind.Gate, GateValue = value };
        public static FloorSpec Bonus => new FloorSpec { Kind = FloorKind.Bonus, GateValue = 1f };

        public static FloorSpec LiveGate(float[] cycle, float period, float phase)
        {
            float max = cycle[0];
            foreach (float v in cycle)
            {
                max = Mathf.Max(max, v);
            }
            return new FloorSpec { Kind = FloorKind.Gate, GateValue = max, Cycle = cycle, Period = period, Phase = phase };
        }

        /// <summary>時刻tのゲート倍率。ゲート以外は1。</summary>
        public float ValueAt(float t)
        {
            if (Kind != FloorKind.Gate)
            {
                return 1f;
            }
            if (!IsLive)
            {
                return GateValue;
            }
            return Cycle[SlotAt(t)];
        }

        private int SlotAt(float t)
        {
            float p = Mathf.Repeat(t + Phase, Period);
            return Mathf.Min(Cycle.Length - 1, Mathf.FloorToInt(p / Period * Cycle.Length));
        }

        /// <summary>倍率が次に変わるまでの秒数(変わらないならfloat.MaxValue)。変わる直前の予告表示に使う。</summary>
        public float TimeToChange(float t)
        {
            if (!IsLive)
            {
                return float.MaxValue;
            }
            float slotLength = Period / Cycle.Length;
            int slot = SlotAt(t);
            float into = Mathf.Repeat(Mathf.Repeat(t + Phase, Period), slotLength);
            float remaining = slotLength - into;
            for (int k = 1; k <= Cycle.Length; k++)
            {
                int next = (slot + k) % Cycle.Length;
                if (!Mathf.Approximately(Cycle[next], Cycle[slot]))
                {
                    return remaining + (k - 1) * slotLength;
                }
            }
            return float.MaxValue;
        }
    }

    /// <summary>
    /// ☆の3条件。すべて画面上に印がある: 建物の☆(ボーナス階)、ゲージの目標(MAX)の☆、ゲージ右端の☆。
    /// </summary>
    public enum StarReason
    {
        BonusFloor,
        Max,
        Overshoot
    }

    /// <summary>
    /// 1ステージ分の定義。ステージ番号から決定的に生成されるので、リトライしても同じ配置になる。
    /// TargetScoreに達するとゲージMAX=クリア。StarScoreまで伸ばすと3つ目の☆が出る(ゲージ右端の☆マーク)。
    /// OptimalScoreは「ゲートが一番良い瞬間を狙って最適に撃ったときの得点」で、ゲージの母数(満点)。
    /// </summary>
    public class StageSpec
    {
        public int StageIndex;
        public FloorSpec[] Floors;
        public int Shots;
        public int OptimalScore;
        public int FirstOptimalTap;
        public int TargetScore;
        public int StarScore;
    }

    /// <summary>
    /// ライブゲート用の共通時計(秒)。ステージ開始で0に戻り、GameManagerが進める。
    /// タップが確定した瞬間の値を使うので、見えている倍率と実際に掛かる倍率が一致する。
    /// QAの自動プレイは、撃つ直前にここへ「狙いの時刻」を直接書き込む。
    /// </summary>
    public static class GateClock
    {
        public static float Now;
    }

    /// <summary>
    /// スコア計算の純粋ロジック。実際の加算・タップ前の予測表示・ステージ生成時の最適解探索で同じ式を共有する。
    /// 1階の得点 = 100 × コンボ倍率 × ゲート倍率(その階までに通過したゲートの積) × ボーナス階の倍率 × (1+歴史解放ボーナス)。
    /// ライブゲートの倍率は時刻tで決まる。最適解は「各ショットを好きな時刻に撃てる」として、
    /// 1周期(GateHorizon秒)の中の最良の時刻を総当たりで調べる(待てば必ず最良の瞬間が来る)。
    /// </summary>
    public static class ScoreRules
    {
        public const int BasePoints = 100;
        public const float ComboBonusPerStep = 0.1f;
        public const float ComboCap = 3f;
        public const float BonusFloorMultiplier = 2f;

        /// <summary>すべてのライブゲートの周期が割り切れる長さ(周期2.4秒と3.6秒の最小公倍数)。</summary>
        public const float GateHorizon = 7.2f;
        public const float GateSampleStep = 0.05f;
        public const int GateSampleCount = 144;
        /// <summary>「人間が確実に合わせられる」窓の半幅(標本数)。±4標本=±0.2秒=0.45秒の窓。☆3つはこの窓で取れる点数で保証する。</summary>
        public const int SustainHalfWidth = 4;

        /// <summary>k番目の標本時刻。倍率の切り替わり(0.05秒の倍数)の真ん中に置く。</summary>
        public static float SampleTime(int k)
        {
            return (k + 0.5f) * GateSampleStep;
        }

        public static float ComboMultiplier(int chainStep)
        {
            return Mathf.Min(1f + ComboBonusPerStep * Mathf.Max(0, chainStep - 1), ComboCap);
        }

        public static int FloorPoints(int chainStep, float gateProduct, float historyBonus, FloorKind kind)
        {
            float kindMultiplier = kind == FloorKind.Bonus ? BonusFloorMultiplier : 1f;
            return Mathf.RoundToInt(BasePoints * ComboMultiplier(chainStep) * gateProduct * kindMultiplier * (1f + historyBonus));
        }

        /// <summary>
        /// 生きている階が[0, limit)の連続区間のとき、時刻tにtapIndexをタップした場合の合計スコアを返す。
        /// </summary>
        public static int SimulateTap(FloorSpec[] floors, int tapIndex, int limit, float historyBonus, float t)
        {
            int score = 0;
            float gateProduct = 1f;
            int step = 0;
            for (int i = tapIndex; i < limit; i++)
            {
                step++;
                FloorSpec f = floors[i];
                if (f.Kind == FloorKind.Gate)
                {
                    gateProduct *= f.ValueAt(t);
                }
                score += FloorPoints(step, gateProduct, historyBonus, f.Kind);
            }
            return score;
        }

        /// <summary>tapIndexで巻き込まれる階にライブゲートがあるか(タイミングで得点が変わるか)。</summary>
        public static bool HasLiveGate(FloorSpec[] floors, int tapIndex, int limit)
        {
            for (int i = tapIndex; i < limit; i++)
            {
                if (floors[i].IsLive)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// tapIndexをタップする場合の、最良の時刻での得点。bestTimeにその時刻を返す。
        /// </summary>
        public static int BestTapScore(FloorSpec[] floors, int tapIndex, int limit, float historyBonus, out float bestTime)
        {
            bestTime = SampleTime(0);
            if (!HasLiveGate(floors, tapIndex, limit))
            {
                return SimulateTap(floors, tapIndex, limit, historyBonus, 0f);
            }

            int best = -1;
            for (int k = 0; k < GateSampleCount; k++)
            {
                float t = SampleTime(k);
                int s = SimulateTap(floors, tapIndex, limit, historyBonus, t);
                if (s > best)
                {
                    best = s;
                    bestTime = t;
                }
            }
            return best;
        }

        /// <summary>
        /// 「どの階をタップすると(残り階limit)最良時刻で何点か」の表。Score[j, limit]とTime[j, limit]。
        /// </summary>
        public sealed class TapTable
        {
            public int N;
            public int[,] Score;
            public float[,] Time;
            /// <summary>0.45秒以上その点数以上が続く窓で取れる最高点(人間が確実に狙える点)。☆3つの保証に使う。</summary>
            public int[,] Sustained;
            public float[,] SustainedTime;
        }

        /// <summary>
        /// tapIndexをタップする場合の、窓(±SustainHalfWidth標本)の間ずっと保たれる最高点。bestTimeは窓の中心の時刻。
        /// 倍率が一瞬だけ揃う瞬間(ピンポイント)は含めない。
        /// </summary>
        public static int SustainedTapScore(FloorSpec[] floors, int tapIndex, int limit, float historyBonus, out float bestTime)
        {
            bestTime = SampleTime(0);
            if (!HasLiveGate(floors, tapIndex, limit))
            {
                return SimulateTap(floors, tapIndex, limit, historyBonus, 0f);
            }

            int count = GateSampleCount;
            int[] s = new int[count];
            for (int k = 0; k < count; k++)
            {
                s[k] = SimulateTap(floors, tapIndex, limit, historyBonus, SampleTime(k));
            }

            int best = -1;
            for (int k = 0; k < count; k++)
            {
                int floor = int.MaxValue;
                for (int d = -SustainHalfWidth; d <= SustainHalfWidth; d++)
                {
                    floor = Mathf.Min(floor, s[((k + d) % count + count) % count]);
                }
                if (floor > best)
                {
                    best = floor;
                    bestTime = SampleTime(k);
                }
            }
            return best;
        }

        public static TapTable BuildTapTable(FloorSpec[] floors, float historyBonus)
        {
            int n = floors.Length;
            TapTable table = new TapTable
            {
                N = n,
                Score = new int[n + 1, n + 1],
                Time = new float[n + 1, n + 1],
                Sustained = new int[n + 1, n + 1],
                SustainedTime = new float[n + 1, n + 1]
            };
            for (int limit = 1; limit <= n; limit++)
            {
                for (int j = 0; j < limit; j++)
                {
                    float bestTime;
                    table.Score[j, limit] = BestTapScore(floors, j, limit, historyBonus, out bestTime);
                    table.Time[j, limit] = bestTime;
                    table.Sustained[j, limit] = SustainedTapScore(floors, j, limit, historyBonus, out bestTime);
                    table.SustainedTime[j, limit] = bestTime;
                }
            }
            return table;
        }

        private static int BestFrom(TapTable table, int limit, int shots, out int firstTap)
        {
            firstTap = -1;
            if (shots <= 0 || limit <= 0)
            {
                return 0;
            }

            int best = 0;
            for (int j = 0; j < limit; j++)
            {
                int ignored;
                int total = table.Score[j, limit] + BestFrom(table, j, shots - 1, out ignored);
                if (total > best)
                {
                    best = total;
                    firstTap = j;
                }
            }
            return best;
        }

        /// <summary>
        /// 残りショット数shotsで得られる最大スコア(各ショットを最良の時刻に撃つ)。firstTapは最初のタップ位置(最適解の一手目)。
        /// </summary>
        public static int BestScore(FloorSpec[] floors, int limit, int shots, float historyBonus, out int firstTap)
        {
            TapTable table = BuildTapTable(floors, historyBonus);
            return BestFrom(table, Mathf.Min(limit, table.N), shots, out firstTap);
        }

        /// <summary>
        /// 最適な撃ち方(タップする階の並びと、それぞれを撃つ時刻)を求める。戻り値はその合計得点。
        /// </summary>
        public static int BestPlan(FloorSpec[] floors, int limit, int shots, float historyBonus, List<int> taps, List<float> times)
        {
            TapTable table = BuildTapTable(floors, historyBonus);
            limit = Mathf.Min(limit, table.N);
            int first;
            int total = BestFrom(table, limit, shots, out first);
            int lim = limit;
            for (int s = shots; s > 0 && lim > 0; s--)
            {
                BestFrom(table, lim, s, out first);
                if (first < 0)
                {
                    break;
                }
                taps.Add(first);
                times.Add(table.Time[first, lim]);
                lim = first;
            }
            return total;
        }

        /// <summary>
        /// 1ショットだけのステージで「時刻を気にせず(最悪の時刻に)撃っても」確実に出せる得点。
        /// 入門ステージで、クリアがタイミング頼みにならないようにするための上限。
        /// </summary>
        public static int GuaranteedSingleTap(FloorSpec[] floors, float historyBonus)
        {
            int n = floors.Length;
            int worst = int.MaxValue;
            for (int k = 0; k < GateSampleCount; k++)
            {
                float t = SampleTime(k);
                int best = 0;
                for (int j = 0; j < n; j++)
                {
                    best = Mathf.Max(best, SimulateTap(floors, j, n, historyBonus, t));
                }
                worst = Mathf.Min(worst, best);
            }
            return worst == int.MaxValue ? 0 : worst;
        }

        /// <summary>
        /// 1発だけ撃つ場合の最高スコア。bonusOnly=trueなら「ボーナス階を巻き込む撃ち方」に限る。
        /// includesBonusは、その最高スコアの撃ち方がボーナス階を巻き込むか。
        /// </summary>
        public static int BestSingleTap(FloorSpec[] floors, float historyBonus, bool bonusOnly, out bool includesBonus)
        {
            // 人間が確実に合わせられる窓で取れる点(Sustained)で比べる。一瞬だけ揃うピンポイントの点は含めない
            TapTable table = BuildTapTable(floors, historyBonus);
            int limit = floors.Length;
            int best = 0;
            includesBonus = false;
            for (int j = 0; j < limit; j++)
            {
                bool hasBonus = ChainIncludesBonus(floors, j, limit);
                if (bonusOnly && !hasBonus)
                {
                    continue;
                }

                int score = table.Sustained[j, limit];
                if (score > best)
                {
                    best = score;
                    includesBonus = hasBonus;
                }
            }
            return best;
        }

        /// <summary>
        /// 「素直に高得点を狙った1発」で☆3つに届くか。ボーナス階を巻き込む1発の最高スコアが☆マーク以上であること。
        /// (1ショットのステージでは、それより高得点になる「ボーナス階を外す1発」があってもいけない。
        /// 外すと高得点でも☆2つ、という不公平が起きるため。)
        /// </summary>
        public static bool OneTapCanEarnAllStars(StageSpec spec, float historyBonus)
        {
            bool ignored;
            int withBonus = BestSingleTap(spec.Floors, historyBonus, true, out ignored);
            if (withBonus < spec.StarScore)
            {
                return false;
            }
            if (spec.Shots <= 1)
            {
                bool bestIncludes;
                BestSingleTap(spec.Floors, historyBonus, false, out bestIncludes);
                return bestIncludes;
            }
            return true;
        }

        /// <summary>
        /// このステージで☆3つ(ボーナス階を壊す・MAXに届く・ゲージ右端の☆まで届く)を全部取れる撃ち方が存在するか。
        /// MAXに届いてもショットと階が残っていれば続けられる実際のルールどおり、
        /// タップ位置が単調に下がる全ての撃ち方を総当たりで調べる。各ショットは「0.45秒以上続く窓」で取れる点数で数える
        /// (ピンポイントでしか取れない点に頼らない)ので、☆3つは人間が確実に合わせられる範囲で取れる。
        /// </summary>
        public static bool AllStarsFeasible(StageSpec spec, float historyBonus)
        {
            return FindThreeStarPlan(spec, historyBonus, null, null);
        }

        /// <summary>
        /// ☆3つを取れる撃ち方(タップする階の並びと、撃つ時刻)を探す。見つかればtrueで、taps/timesに順番に入れる(nullなら存在確認のみ)。
        /// 実機同様の自動プレイ検証(StageAutoPlayer)が、この手順をそのまま実行して本当に☆3つになるか確かめる。
        /// </summary>
        public static bool FindThreeStarPlan(StageSpec spec, float historyBonus, List<int> taps, List<float> times)
        {
            TapTable table = BuildTapTable(spec.Floors, historyBonus);
            return PlanSearch(spec, table, spec.Floors.Length, spec.Shots, 0, false, taps, times);
        }

        private static bool PlanSearch(StageSpec spec, TapTable table, int limit, int shotsLeft, int score, bool bonus,
            List<int> taps, List<float> times)
        {
            for (int j = 0; j < limit; j++)
            {
                int total = score + table.Sustained[j, limit];
                bool gotBonus = bonus || ChainIncludesBonus(spec.Floors, j, limit);

                if (taps != null)
                {
                    taps.Add(j);
                    times.Add(table.SustainedTime[j, limit]);
                }

                if (gotBonus && total >= spec.StarScore)
                {
                    return true;
                }
                if (shotsLeft > 1 && j > 0 && PlanSearch(spec, table, j, shotsLeft - 1, total, gotBonus, taps, times))
                {
                    return true;
                }

                if (taps != null)
                {
                    taps.RemoveAt(taps.Count - 1);
                    times.RemoveAt(times.Count - 1);
                }
            }
            return false;
        }

        /// <summary>
        /// tapIndexをタップして巻き込まれる階(tapIndex以上limit未満)にボーナス階が含まれるか。
        /// </summary>
        public static bool ChainIncludesBonus(FloorSpec[] floors, int tapIndex, int limit)
        {
            for (int i = tapIndex; i < limit; i++)
            {
                if (floors[i].Kind == FloorKind.Bonus)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
