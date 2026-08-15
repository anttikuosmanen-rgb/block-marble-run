using System.Collections;
using System.Collections.Generic;
using BlockMarbleRun.Grid;
using UnityEngine;

namespace BlockMarbleRun.Play
{
    /// <summary>
    /// Releases balls one at a time and reports the average of the runs, with the spread.
    ///
    /// A single run says almost nothing here: the same track has finished anywhere between stalling
    /// halfway and clearing the ramp, and a change that helps is indistinguishable from a lucky
    /// release. Ten runs and a standard deviation separate the two, and the spread is as much the
    /// answer as the mean - "it varies wildly" is a measurement, not a complaint.
    /// </summary>
    public sealed class RunTester : MonoBehaviour
    {
        public PlayController play;

        [Tooltip("How many balls to release, one after another.")]
        public int runs = 10;

        [Tooltip("Below this speed for a moment, a ball counts as come to rest.")]
        public float stillSpeed = 0.15f;

        [Tooltip("How long it must stay that slow before the run is called finished.")]
        public float stillSeconds = 0.4f;

        [Tooltip("Cap on a single run. A ball still going at this point is cut short and counted as such.")]
        public float timeoutSeconds = 4f;

        public bool Running { get; private set; }
        public string Report { get; private set; } = "";
        public int Completed { get; private set; }

        struct RunResult
        {
            public float PeakSpeed;      // world units per second
            public float ClimbAtLowest;  // layers the ball could still have climbed at its lowest point
            public float EnergyKept;     // percent of the drop still in hand there
            public float Seconds;
            public int Contacts;
            public bool Finished;
            public bool Lost;
            public bool CutShort;
        }

        public void Begin()
        {
            if (Running)
                return;

            StartCoroutine(RunBatch());
        }

        IEnumerator RunBatch()
        {
            Running = true;
            Completed = 0;
            Report = "running...";

            var results = new List<RunResult>(runs);

            for (int i = 0; i < runs && Running; i++)
            {
                // Leaving play mode ends the batch: the build is being edited underneath it.
                if (!play.Active)
                    break;

                play.Reset();
                yield return new WaitForFixedUpdate();

                Marble marble = play.ReleaseOne();
                if (marble == null)
                {
                    Report = "No start piece marked - point at a dead end in build mode and press X";
                    Running = false;
                    yield break;
                }

                yield return MeasureRun(marble, results);

                Completed = results.Count;
            }

            Report = Summarise(results);
            Running = false;
        }

        IEnumerator MeasureRun(Marble marble, List<RunResult> results)
        {
            var result = new RunResult();

            float lowest = float.PositiveInfinity;
            float still = 0f;
            float elapsed = 0f;
            int startFinished = play.Finished;
            int startLost = play.Lost;

            while (elapsed < timeoutSeconds)
            {
                yield return new WaitForFixedUpdate();
                elapsed += Time.fixedDeltaTime;

                if (!Running || !play.Active)
                    break;

                // Retired by the goal or the kill height: the run is over either way.
                if (marble == null)
                {
                    result.Finished = play.Finished > startFinished;
                    result.Lost = play.Lost > startLost;
                    break;
                }

                float speed = marble.Body.linearVelocity.magnitude;
                result.PeakSpeed = Mathf.Max(result.PeakSpeed, speed);

                float y = marble.transform.position.y;
                if (y < lowest)
                {
                    // Sampled at the lowest point rather than at the end, because that is where the
                    // descent has finished paying in and the climb has not yet started spending.
                    lowest = y;

                    float g = Mathf.Abs(Physics.gravity.y);
                    float climb = g > 0f ? 7f * speed * speed / (20f * g) / GridCoord.BrickUnits : 0f;
                    float dropped = (marble.PeakHeight - y) / GridCoord.BrickUnits;

                    result.ClimbAtLowest = climb;
                    result.EnergyKept = dropped > 0.1f ? 100f * climb / dropped : 0f;
                }

                still = speed < stillSpeed ? still + Time.fixedDeltaTime : 0f;

                if (still >= stillSeconds)
                    break;
            }

            // Reported separately: with a short cap a ball that is still rolling looks identical to
            // one that came to rest, and those mean opposite things about the run.
            result.CutShort = elapsed >= timeoutSeconds;
            result.Seconds = elapsed;

            if (marble != null)
                result.Contacts = marble.TotalContacts;

            results.Add(result);
        }

        public void Stop() => Running = false;

        static string Summarise(List<RunResult> results)
        {
            if (results.Count == 0)
                return "no runs";

            var peak = new List<float>(results.Count);
            var climb = new List<float>(results.Count);
            var kept = new List<float>(results.Count);
            var contacts = new List<float>(results.Count);
            var seconds = new List<float>(results.Count);

            int finished = 0, lost = 0, cut = 0;

            foreach (RunResult r in results)
            {
                peak.Add(PlayController.ToMetresPerSecond(r.PeakSpeed));
                climb.Add(r.ClimbAtLowest);
                kept.Add(r.EnergyKept);
                contacts.Add(r.Contacts);
                seconds.Add(r.Seconds);

                if (r.Finished) finished++;
                if (r.Lost) lost++;
                if (r.CutShort) cut++;
            }

            return
                $"{results.Count} runs\n" +
                $"peak speed   {Stat(peak, "0.00")} m/s\n" +
                $"climb spare  {Stat(climb, "0.00")} layers\n" +
                $"energy kept  {Stat(kept, "0")} %\n" +
                $"contacts     {Stat(contacts, "0")}\n" +
                $"duration     {Stat(seconds, "0.0")} s\n" +
                $"finished {finished}/{results.Count}   lost {lost}/{results.Count}   " +
                $"cut short {cut}/{results.Count}";
        }

        /// <summary>
        /// Mean, standard deviation and range together. The mean alone would hide exactly the thing
        /// under investigation, which is how much the same track varies between identical releases.
        /// </summary>
        static string Stat(List<float> values, string format)
        {
            float sum = 0f, min = float.PositiveInfinity, max = float.NegativeInfinity;

            foreach (float v in values)
            {
                sum += v;
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
            }

            float mean = sum / values.Count;

            float variance = 0f;
            foreach (float v in values)
                variance += (v - mean) * (v - mean);

            float deviation = Mathf.Sqrt(variance / values.Count);

            return $"{mean.ToString(format)} ± {deviation.ToString(format)}   " +
                   $"({min.ToString(format)} – {max.ToString(format)})";
        }
    }
}
