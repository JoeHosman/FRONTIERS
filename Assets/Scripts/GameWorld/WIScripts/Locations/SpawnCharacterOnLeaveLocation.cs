using UnityEngine;
using System.Collections;
using System;
using Frontiers;

namespace Frontiers.World.WIScripts
{
		public class SpawnCharacterOnLeaveLocation : WIScript
		{
				public SpawnCharacterOnLeaveLocationState State = new SpawnCharacterOnLeaveLocationState();
				Visitable visitable = null;
				Structure structure = null;

				public override void OnInitialized()
				{
						Visitable visitable = null;
						Structure structure = null;
						if (worlditem.Is <Visitable>(out visitable)) {
								visitable.OnPlayerLeave += OnPlayerLeave;
						} else if (worlditem.Is <Structure>(out structure)) {
								structure.OnPlayerExit += OnPlayerLeave;
						}
				}

				public void OnPlayerLeave()
				{
						if (!mCheckingConditionOverTime) {
								mCheckingConditionOverTime = true;
								StartCoroutine(CheckConditionOverTime());
						}
				}

				protected IEnumerator CheckConditionOverTime()
				{
						//wait for a second because missions may need to update in response to leaving this location
						double waitUntil = Frontiers.WorldClock.AdjustedRealTime + 2.5f;
						while (Frontiers.WorldClock.AdjustedRealTime < waitUntil) {
								yield return null;
						}
						//see if our mission conditions are met
						if (MissionCondition.CheckCondition(State.Condition)) {
								//create a spawner that follows the player around
								//this will ensure that even if the location is unloaded
								//the character will still be spawned
								Player.Local.CharacterSpawner.AddSpawnRequest(State.Request);
								if (State.FinishOnSpawn) {
										Finish();
								}
						}
						mCheckingConditionOverTime = false;
						yield break;
				}

				protected bool mCheckingConditionOverTime = false;
		}

		[Serializable]
		public class SpawnCharacterOnLeaveLocationState
		{
				public MissionCondition Condition = new MissionCondition();
				public CharacterSpawnRequest Request = new CharacterSpawnRequest();
				public bool FinishOnSpawn = true;
				public bool WaitForMissionConditionOnLeave = true;
		}
}