using Aicup2020.Model;

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;

using Action = Aicup2020.Model.Action;

namespace Aicup2020
{
	public class MyStrategy
	{
		private enum CellType : byte
		{
			Free,
			Resource,
			Builder,
			Army,
			Building,
			Enemy
		}

		private readonly CellType[][] _cells = new CellType[80][];

		private readonly EntityType[] _emptyEntityTypes = Array.Empty<EntityType>();
		private readonly EntityType[] _resourceEntityTypes = new EntityType[1] { EntityType.Resource };

		private struct BuilderTask
		{
			public int MaxWorkers { get; }

			public bool IsHouse { get; }

			public ActionType ActionType { get; }

			public int CurrentWorkers => _participants.Count;

			public int MinFreeWorkers { get; }

			public RepairAction? RepairAction { get; }

			public BuildAction? BuildAction { get; }

			private List<int> _participants;

			public BuilderTask(int maxWorkers, ActionType actionType, RepairAction? repairAction, BuildAction? buildAction, EntityType entityType, int minFreeWorkers)
			{
				MaxWorkers = maxWorkers;
				ActionType = actionType;
				RepairAction = repairAction;
				BuildAction = buildAction;
				_participants = new List<int>(MaxWorkers);
				IsHouse = entityType == EntityType.House;
				MinFreeWorkers = minFreeWorkers;
			}

			public void SetParticipants(List<int> newParticipants)
			{
				_participants = newParticipants;
			}

			public IReadOnlyList<int> Participants => _participants;

			public void Add(int entityId)
			{
				_participants.Add(entityId);
			}

			public void Remove(int entityId)
			{
				_participants.Remove(entityId);
				_freeBuilders.Remove(entityId);
			}
		}

		private List<BuilderTask> _builderTasks = new(4);
		private List<BuilderTask> _newBuilderTasks = new(4);
		private static readonly SortedDictionary<int, Entity> _freeBuilders = new ();
		private int MaxHouseRepairWorkers = 3;
		private const int MaxRangedBaseRepairWorkers = 10;
		private const int MaxBaseRapairWorkers = 5;

		private int _lostResources;

		// Player info
		private Player _selfInfo;
		private readonly List<Entity> myEntities = new List<Entity>(10);
		private readonly List<Entity> builders = new List<Entity>(10);
		private readonly List<Entity> rangedUnits = new List<Entity>(10);
		private readonly List<Entity> meleeUnits = new List<Entity>(10);
		private readonly List<Entity> houses = new List<Entity>(10);
		private readonly List<Entity> builderBases = new List<Entity>(1);
		private readonly List<Entity> rangedBases = new List<Entity>(1);
		private readonly List<Entity> meleeBases = new List<Entity>(1);
		private readonly List<Entity> turrets = new List<Entity>(1);
		private readonly List<Entity> allBuildings = new List<Entity>(3);

		private readonly List<Entity> resources = new List<Entity>(1000);
		private readonly List<Entity> otherEntities = new List<Entity>(100);
		private readonly List<Entity> _enemyTroops = new List<Entity>(10);

		private const int MyPositionEdge = 35;
		private const double enemyRadiusDetection = 6.5d;
		private List<Entity> _leftEnemiesInMyBase = new(2);
		private List<Entity> _rightEnemiesInMyBase = new(2);
		private Party _leftDefenseParty = new(7, PartyType.Defense, new Vec2Int(9, 20), DefensePosition.Left);
		private Party _rightDefenseParty = new(7, PartyType.Defense, new Vec2Int(20, 9), DefensePosition.Right);
		private Party _firstAttackParty = new(20, PartyType.Attack, new Vec2Int(20, 20), DefensePosition.None);

		private readonly Dictionary<int, EntityAction> _entityActions = new Dictionary<int, EntityAction>(10);
		private Vec2Int _myBaseCenter = new Vec2Int(5, 5);

		private int _totalFood = 0;
		private int _consumedFood = 0;

		private Vec2Int _moveTo = new Vec2Int(76, 4);
		private int _angle = 0;
		private int Stage = 0;

		private int _leftNearEnemyCount = 0;
		private int _rightNearEnemyCount = 0;
		private int _leftFarEnemyCount = 0;
		private int _rightFarEnemyCount = 0;

		private const float _buildersPercentage = 0.30f;

		private EntityProperties houseProperties;
		private EntityProperties builderBaseProperties;
		private EntityProperties rangedBaseProperties;
		private EntityProperties meleeBaseProperties;
		private EntityProperties builderUnitProperties;
		private EntityProperties rangedUnitProperties;
		private EntityProperties meleeUnitProperties;
		private EntityProperties wallProperties;
		private EntityProperties turretProperties;
		private EntityProperties resourceProperties;

		private int turn = 0;

		public MyStrategy()
		{
			for (int i = 0; i < 80; i++)
			{
				_cells[i] = new CellType[80];
			}
		}

		private struct BuildersHold
		{
			public Vec2Int PrevPosition;

			public int TickCount;

			public BuildersHold(int tickCount, Vec2Int prevPosition)
			{
				TickCount = tickCount;
				PrevPosition = prevPosition;
			}
		}

		public Action GetAction(PlayerView playerView, DebugInterface debugInterface)
		{
			turn++;
			_entityActions.Clear();

			Action result = new Action(_entityActions);

			foreach (Player player in playerView.Players)
			{
				if (player.Id == playerView.MyId)
				{
					_selfInfo = player;
					_lostResources = player.Resource;
					break;
				}
			}

			houseProperties = playerView.EntityProperties[EntityType.House];
			builderBaseProperties = playerView.EntityProperties[EntityType.BuilderBase];
			rangedBaseProperties = playerView.EntityProperties[EntityType.RangedBase];
			meleeBaseProperties = playerView.EntityProperties[EntityType.MeleeBase];
			wallProperties = playerView.EntityProperties[EntityType.Wall];
			builderUnitProperties = playerView.EntityProperties[EntityType.BuilderUnit];
			rangedUnitProperties = playerView.EntityProperties[EntityType.RangedUnit];
			meleeUnitProperties = playerView.EntityProperties[EntityType.MeleeUnit];
			turretProperties = playerView.EntityProperties[EntityType.Turret];
			resourceProperties = playerView.EntityProperties[EntityType.Resource];

			ClearLists();

			CollectEntities(playerView);

			MaxHouseRepairWorkers = builders.Count >= 15 ? 3 : 2;

			int buildersCount = builders.Count;

			_consumedFood = buildersCount + rangedUnits.Count + meleeUnits.Count;
			_totalFood = builderBases.Where(q => q.Active).Count() * builderBaseProperties.PopulationProvide
						+ rangedBases.Where(q => q.Active).Count() * rangedBaseProperties.PopulationProvide
						+ meleeBases.Where(q => q.Active).Count() * meleeBaseProperties.PopulationProvide
						+ houses.Where(q => q.Active).Count() * houseProperties.PopulationProvide;

			int lostResources = _lostResources;

			ExecuteTasks(ref _builderTasks, false, in result, ref lostResources);

			int rangedBasesTasksCount = _builderTasks.Where(q => !q.IsHouse).Count(); ;

			if (rangedBases.Count == 0 && rangedBasesTasksCount == 0 && _totalFood <= 25)
				Stage = 1;
			else if ((rangedBases.Count > 0 || rangedBasesTasksCount > 0) && _totalFood <= 25)
				Stage = 2;
			else if ((rangedBases.Count > 0 || rangedBasesTasksCount > 0) && _totalFood >= 25)
				Stage = 3;

			FindNewBuilderTasks(ref lostResources);

			ExecuteTasks(ref _newBuilderTasks, true, in result, ref lostResources);

			_builderTasks.AddRange(_newBuilderTasks);
			_newBuilderTasks.Clear();

			foreach(Entity builder in _freeBuilders.Values)
			{
				if(BuilderCheckThreats(builder, in result))
				{
					continue;
				}

				result.EntityActions[builder.Id] = new EntityAction(null, null, new AttackAction(null, new AutoAttack(60, _resourceEntityTypes)), null);
			}

			rangedBases.ForEach(rangedBase => RangedBaseLogic(rangedBase, ref result, ref lostResources));

			builderBases.ForEach(builderBase => BuilderBaseLogic(builderBase, ref result, ref lostResources));

			meleeBases.ForEach(meleeBase => MeleeBaseLogic(playerView, meleeBase, ref result, ref lostResources));

			rangedUnits.ForEach(rangedUnit => BattleUnitLogic(rangedUnit, in result));

			meleeUnits.ForEach(meleeUnit => BattleUnitLogic(meleeUnit, in result));

			turrets.ForEach(turret => result.EntityActions[turret.Id] = new EntityAction(null, null, new AttackAction(null, new AutoAttack(turretProperties.SightRange, _emptyEntityTypes)), null));

			_leftDefenseParty.Clear();
			_rightDefenseParty.Clear();
			_firstAttackParty.Clear();

			return result;
		}

		private void MeleeBaseLogic(PlayerView playerView, Entity meleeBase, ref Action result, ref int lostResources)
		{
			if (rangedBases.Count > 0)
				return;

			EntityType buildingEntityType = meleeBaseProperties.Build.Value.Options[0];

			if (Stage == 3 && ((lostResources - meleeUnitProperties.InitialCost) >= 0))
			{
				BuildUnitAction(meleeBase, buildingEntityType, meleeBaseProperties, in result, ref lostResources);
			}
		}

		private void RangedBaseLogic(Entity rangedBase, ref Action result, ref int lostResources)
		{
			EntityType buildingEntityType = rangedBaseProperties.Build.Value.Options[0];

			if (Stage == 3 && ((lostResources - rangedUnitProperties.InitialCost) >= 0))
			{
				BuildUnitAction(rangedBase, buildingEntityType, rangedBaseProperties, in result, ref lostResources);
			}
		}

		private void BuilderBaseLogic(Entity builderBase, ref Action result, ref int lostResources)
		{
			bool rangedBasesExists = rangedBases.Where(q => q.Active).Any();

			bool needBuildersWhenFirstStage = builders.Count < 25;
			bool needBuildersWhenSecondStage = lostResources >= 100;
			bool needBuildersWhenThirdStage = ((float)(builders.Count - 25) / _totalFood) <= _buildersPercentage;

			EntityType buildingEntityType = builderBaseProperties.Build.Value.Options[0];

			int nearEnemyCount = _leftNearEnemyCount + _rightNearEnemyCount;
			if (((Stage == 1 && needBuildersWhenFirstStage) || (Stage == 2 && needBuildersWhenSecondStage) || (Stage == 3 && needBuildersWhenThirdStage))
				&& ((lostResources - builderUnitProperties.InitialCost) >= 0)
				&& (nearEnemyCount == 0 || (nearEnemyCount < (_leftDefenseParty.Count + _rightDefenseParty.Count))))
			{
				BuildUnitAction(builderBase, buildingEntityType, builderBaseProperties, in result, ref lostResources);
			}
		}

		private void ExecuteTasks(ref List<BuilderTask> builderTasks, bool ignoreCheckFreeLocation, in Action result, ref int lostResources)
		{
			List<BuilderTask> notCompletedTasks = new(builderTasks.Count);
			foreach (BuilderTask builderTask in builderTasks)
			{
				bool complete = false;
				bool isBuildTask = builderTask.ActionType == ActionType.Build;
				Vec2Int builderPosition = default;
				EntityProperties buildingProperties = builderTask.IsHouse ? houseProperties : rangedBaseProperties;

				if (isBuildTask && !builderTask.BuildAction.HasValue)
				{
					builderPosition = builderTask.BuildAction.Value.Position;
					complete = true;
					break;
				}

				if (!isBuildTask && !builderTask.RepairAction.HasValue)
				{
					complete = true;
					break;
				}

				if(builderTask.CurrentWorkers < builderTask.MaxWorkers)
				{
					List<int> nearestFreeBuilders = GetNearestFreeBuilders(builderPosition, buildingProperties.Size);
					int dif = builderTask.MaxWorkers - builderTask.CurrentWorkers;
					for (int i = 0; i < dif && i < nearestFreeBuilders.Count; i++)
						builderTask.Add(nearestFreeBuilders[i]);
				}

				List<int> backup = new(builderTask.Participants.Count);
				foreach (int builderId in builderTask.Participants)
				{
					if (_freeBuilders.TryGetValue(builderId, out Entity builder))
					{
						if (BuilderCheckThreats(builder, in result))
						{
							_freeBuilders.Remove(builderId);
							continue;
						}

						if (isBuildTask)
						{
							BuildAction buildAction = builderTask.BuildAction.Value;
							if (BuildBuildingLogic(ref builder, in buildAction, in result, ignoreCheckFreeLocation, out complete))
							{
								if (complete)
								{
									break;
								}
							}
							else
							{
								complete = true;
								break;
							}
						}
						else
						{
							RepairAction repairAction = builderTask.RepairAction.Value;
							if (RepairLogic(ref builder, in repairAction, in result, out complete))
							{
								if (complete)
								{
									break;
								}
							}
							else
							{
								complete = true;
								break;
							}
						}

						_freeBuilders.Remove(builderId);
						backup.Add(builderId);
					}
				}

				if (!complete)
				{
					builderTask.SetParticipants(backup);

					if (isBuildTask)
					{
						BuildAction buildAction = builderTask.BuildAction.Value;
						lostResources -= buildingProperties.InitialCost;
						LockBuildingPosition(buildAction.Position, buildingProperties.Size);
					}

					notCompletedTasks.Add(builderTask);
				}
			}

			builderTasks = notCompletedTasks;
		}

		private bool BuilderCheckThreats(Entity builder, in Action result)
		{
			IEnumerable<Entity> threatsWorker = _enemyTroops.Where(q => Math.Abs(FindDistance(q.Position, builder.Position)) < enemyRadiusDetection);
			if (threatsWorker.Any())
			{
				result.EntityActions[builder.Id] = new EntityAction(new MoveAction(_myBaseCenter, true, false), null, null, null);
				return true;
			}

			return false;
		}

		private void BattleUnitLogic(Entity unit, in Action result)
		{
			if (FindDistance(unit.Position, _moveTo) <= 4.0f)
			{
				switch (_angle)
				{
					case 0:
						_moveTo.X = 4;
						_moveTo.Y = 76;
						break;
					case 1:
						_moveTo.X = 78;
						_moveTo.Y = 78;
						break;
					default:
						if (otherEntities.Count > 0)
						{
							_moveTo = otherEntities[0].Position;
						}
						else
						{
							_moveTo.X = 78;
							_moveTo.Y = 4;
							_angle = -1;
						}
						break;
				}

				_firstAttackParty.IsAttacking = false;
				_angle++;
			}

			EntityProperties entityProperties = GetEntityProperties(unit.EntityType);

			if (TryGetParty(unit.Id, out Party party))
			{
				party.Turn(unit.Id);

				if (party.PartyType == PartyType.Defense)
				{
					if (party.DefensePosition == DefensePosition.Left)
					{
						LeftDefensePartyLogic(ref party, entityProperties.SightRange, in result);
						return;
					}

					if (party.DefensePosition == DefensePosition.Right)
					{
						RightDefensePartyLogic(ref party, entityProperties.SightRange, in result);
						return;
					}

					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(party.MoveTo, false, false), null, new AttackAction(null, new AutoAttack(entityProperties.SightRange, _emptyEntityTypes)), null);
					return;
				}
				else if (party.PartyType == PartyType.Attack)
				{
					FirstAttackPartyLogic(entityProperties.SightRange, in result);
					return;
				}
			}

			if (_leftDefenseParty.Count < _leftDefenseParty.Capacity)
			{
				_leftDefenseParty.Add(unit.Id);
				LeftDefensePartyLogic(ref party, entityProperties.SightRange, in result);
				return;
			}
			else if (_rightDefenseParty.Count < _rightDefenseParty.Capacity)
			{
				_rightDefenseParty.Add(unit.Id);
				RightDefensePartyLogic(ref party, entityProperties.SightRange, in result);
				return;
			}

			bool prevAttackState = _firstAttackParty.IsAttacking;
			_firstAttackParty.Add(unit.Id);
			FirstAttackPartyLogicLast(entityProperties.SightRange, in result, out MoveAction moveAction);

			if (prevAttackState == false && _firstAttackParty.IsAttacking == true)
			{
				foreach (int id in _firstAttackParty.Participants)
				{
					result.EntityActions[id] = result.EntityActions[unit.Id] = new EntityAction(moveAction, null, new AttackAction(null, new AutoAttack(entityProperties.SightRange, _emptyEntityTypes)), null); ;
				}
			}

			result.EntityActions[unit.Id] = new EntityAction(new MoveAction(_firstAttackParty.MoveTo, true, false), null, new AttackAction(null, new AutoAttack(entityProperties.SightRange, _emptyEntityTypes)), null);

			void FirstAttackPartyLogic(int sightRange, in Action result) => FirstAttackPartyLogicLast(sightRange, in result, out MoveAction _);

			void FirstAttackPartyLogicLast(int sightRange, in Action result, out MoveAction moveAction)
			{
				if (_firstAttackParty.IsAttacking)
				{
					if (_firstAttackParty.Count <= 5)
					{
						_firstAttackParty.IsAttacking = false;
					}
					else
					{
						if(_rightNearEnemyCount > 0 && _rightNearEnemyCount >= _leftNearEnemyCount)
						{
							moveAction = new MoveAction(new Vec2Int(78, 2), true, true);
						}
						else if (_leftNearEnemyCount > 0)
						{
							moveAction = new MoveAction(new Vec2Int(2,78), true, true);
						}
						else if (_rightFarEnemyCount > 0 && _rightFarEnemyCount >= _leftFarEnemyCount)
						{
							moveAction = new MoveAction(new Vec2Int(78, 2), true, true);
						}
						else if (_leftFarEnemyCount > 0)
						{
							moveAction = new MoveAction(new Vec2Int(2, 78), true, true);
						}
						else
						{
							moveAction = new MoveAction(_moveTo, true, true);
						}

						result.EntityActions[unit.Id] = new EntityAction(moveAction, null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
					}
				}

				if (_firstAttackParty.Count >= _firstAttackParty.Capacity)
				{
					_firstAttackParty.IsAttacking = true;
					moveAction = new MoveAction(_moveTo, true, true);
					result.EntityActions[unit.Id] = new EntityAction(moveAction, null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
				else
				{
					moveAction = new MoveAction(_firstAttackParty.MoveTo, false, false);
					result.EntityActions[unit.Id] = new EntityAction(moveAction, null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
			}

			void LeftDefensePartyLogic(ref Party party, int sightRange, in Action result)
			{
				if (_leftEnemiesInMyBase.Count > 0)
				{
					Entity enemy = _leftEnemiesInMyBase[0];
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(enemy.Position, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
				else if (_rightEnemiesInMyBase.Count > 0 && _rightDefenseParty.Count == 0)
				{
					Entity enemy = _rightEnemiesInMyBase[0];
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(enemy.Position, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
				else
				{
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(party.MoveTo, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
			}

			void RightDefensePartyLogic(ref Party party, int sightRange, in Action result)
			{
				if (_rightEnemiesInMyBase.Count > 0)
				{
					Entity enemy = _rightEnemiesInMyBase[0];
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(enemy.Position, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
				else if (_leftEnemiesInMyBase.Count > 0 && _leftDefenseParty.Count == 0)
				{
					Entity enemy = _leftEnemiesInMyBase[0];
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(enemy.Position, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
				else
				{
					result.EntityActions[unit.Id] = new EntityAction(new MoveAction(party.MoveTo, true, false), null, new AttackAction(null, new AutoAttack(sightRange, _emptyEntityTypes)), null);
				}
			}

		}

		private List<int> GetNearestFreeBuilders(Vec2Int buildingPosition, int size)
		{
			int mid = size / 2 + 1;
			Vec2Int buildingCenterPosition = new(buildingPosition.X + mid, buildingPosition.Y + mid);
			return _freeBuilders.OrderBy(q => FindDistance(buildingCenterPosition, q.Value.Position)).Select(q=>q.Key).ToList();
		}

		private void FindNewBuilderTasks(ref int lostResources)
		{
			foreach(Entity rangedBase in rangedBases)
			{
				if(rangedBase.Active && rangedBase.Health < rangedBaseProperties.MaxHealth && !_newBuilderTasks.Where(q=>q.RepairAction.HasValue && q.RepairAction.Value.Target == rangedBase.Id).Any())
				{
					BuilderTask builderTask = new BuilderTask(MaxBaseRapairWorkers, ActionType.Repair, new RepairAction(rangedBase.Id), null, EntityType.RangedBase, 10);
					_newBuilderTasks.Add(builderTask);
				}
			}

			foreach (Entity meleeBase in rangedBases)
			{
				if (meleeBase.Active && meleeBase.Health < meleeBaseProperties.MaxHealth && !_newBuilderTasks.Where(q => q.RepairAction.HasValue && q.RepairAction.Value.Target == meleeBase.Id).Any())
				{
					BuilderTask builderTask = new BuilderTask(MaxBaseRapairWorkers, ActionType.Repair, new RepairAction(meleeBase.Id), null, EntityType.MeleeBase, 10);
					_newBuilderTasks.Add(builderTask);
				}
			}

			foreach (Entity builderBase in builderBases)
			{
				if (builderBase.Active && builderBase.Health < builderBaseProperties.MaxHealth && !_newBuilderTasks.Where(q => q.RepairAction.HasValue && q.RepairAction.Value.Target == builderBase.Id).Any())
				{
					BuilderTask builderTask = new BuilderTask(MaxBaseRapairWorkers, ActionType.Repair, new RepairAction(builderBase.Id), null, EntityType.BuilderBase, 10);
					_newBuilderTasks.Add(builderTask);
				}
			}

			int housesTasksCount = _builderTasks.Where(q => q.IsHouse).Count();
			int rangedBasesTasksCount = _builderTasks.Count - housesTasksCount;

			if (rangedBases.Count == 0
				&& rangedBasesTasksCount == 0
				&& lostResources >= rangedBaseProperties.InitialCost
				&& TryGetFreeLocation(rangedBaseProperties.Size, out Vec2Int buildingPositon, out Vec2Int moveToBuilderPosition, true))
			{
				lostResources -= rangedBaseProperties.InitialCost;
				BuilderTask builderTask = new BuilderTask(1, ActionType.Build, null, new BuildAction(EntityType.RangedBase, buildingPositon), EntityType.RangedBase, 10);

				_newBuilderTasks.Add(builderTask);
				LockBuildingPosition(buildingPositon, rangedBaseProperties.Size);
			}

			for(int i = 0; i <= 3; i++)
			{
				int foodDeference = _totalFood - _consumedFood;
				int newHousesTasksCount = _newBuilderTasks.Where(q => q.IsHouse).Count();
				int totalHousesTasksCount = housesTasksCount + newHousesTasksCount;

				bool needHouseWhenFirstStage = _totalFood + totalHousesTasksCount * houseProperties.PopulationProvide <= 20;
				bool needHouseWhenSecondStage = totalHousesTasksCount < 2;
				bool needHouseWhenThirdStage = foodDeference < 10 && totalHousesTasksCount < 3;
				bool needAdditionalHouseWhenThirdStage = foodDeference < 15 && (totalHousesTasksCount < 4 && lostResources >= 200);

				if ( ((Stage == 1 && needHouseWhenFirstStage) || (Stage == 2 && needHouseWhenSecondStage) || (Stage == 3 && (needHouseWhenThirdStage || needAdditionalHouseWhenThirdStage)))
					&& lostResources >= houseProperties.InitialCost 
					&& TryGetFreeLocation(houseProperties.Size, out Vec2Int housePosition, out moveToBuilderPosition))
				{
					lostResources -= houseProperties.InitialCost;
					BuilderTask builderTask = new BuilderTask(1, ActionType.Build, null, new BuildAction(EntityType.House, housePosition), EntityType.House, 0);
					_newBuilderTasks.Add(builderTask);
					LockBuildingPosition(housePosition, rangedBaseProperties.Size);
					continue;
				}

				break;
			}
		}

		private bool RepairLogic(ref Entity builder, in RepairAction repairAction, in Action result, out bool complete)
		{
			complete = false;
			if (TryGetBuilding(repairAction.Target, out Entity repairingBuilding))
			{
				EntityProperties repairTargetProperties = GetEntityProperties(repairingBuilding.EntityType);
				if (repairingBuilding.Health == repairTargetProperties.MaxHealth)
				{
					complete = true;
					return true;
				}

				if(CheckBuilderOnBuildingEdge(repairingBuilding.Position, builder.Position, repairTargetProperties.Size))
				{
					result.EntityActions[builder.Id] = new EntityAction(null, null, null, repairAction);
					return true;
				}

				if(TryGetFreeUnitLocation(repairingBuilding.Position, repairTargetProperties.Size, out Vec2Int moveToBuilderPosition))
				{
					result.EntityActions[builder.Id] = new EntityAction(new MoveAction(moveToBuilderPosition, true, false), null, null, repairAction);
					return true;
				}
			}

			return false;
		}

		private bool BuildBuildingLogic(ref Entity builder, in BuildAction buildAction, in Action result, bool ignoreCheckFreeLocation, out bool complete)
		{
			complete = false;
			EntityType buildingType = buildAction.EntityType;
			EntityProperties buildingProperties = GetEntityProperties(buildingType);

			IEnumerable<Entity> entities = (buildAction.EntityType == EntityType.House ? houses : rangedBases).Where(q => q.Active == false);

			Vec2Int buildingPosition = buildAction.Position;
			Vec2Int builderPosition = builder.Position;
			if (entities.Any())
			{
				IEnumerable<Entity> inactiveBuildings = entities.Where(q => q.Position.X == buildingPosition.X && q.Position.Y == buildingPosition.Y && buildingType == q.EntityType);

				if (inactiveBuildings.Any())
				{
					complete = true;

					Entity lastBuilding = inactiveBuildings.First();
					_newBuilderTasks.Add(new BuilderTask(buildingType == EntityType.House ? MaxHouseRepairWorkers : MaxRangedBaseRepairWorkers, ActionType.Repair, new RepairAction(lastBuilding.Id), null, buildingType, buildingType == EntityType.House ? 0 : 15));
					return true;
				}
			}

			bool ignoreBuildingBuffer = buildingType == EntityType.RangedBase;

			if (ignoreCheckFreeLocation || CheckFreeLocation(buildingPosition, buildingProperties.Size, ignoreBuildingBuffer))
			{
				if (CheckBuilderOnBuildingEdge(buildingPosition, builderPosition, buildingProperties.Size))
				{
					LockBuildingPosition(buildingPosition, buildingProperties.Size);
					result.EntityActions[builder.Id] = new EntityAction(null, buildAction, null, null);
					return true;
				}

				if(TryGetFreeUnitLocation(buildingPosition, buildingProperties.Size, out builderPosition))
				{
					LockBuildingPosition(buildingPosition, buildingProperties.Size);
					result.EntityActions[builder.Id] = new EntityAction(new MoveAction(builderPosition, true, false), buildAction, null, null);
					return true;
				}
			}

			if (TryGetFreeLocation(buildingProperties.Size, out buildingPosition, out Vec2Int newBuilderPosition, ignoreBuildingBuffer))
			{
				if(CheckBuilderOnBuildingEdge(buildingPosition, builderPosition, buildingProperties.Size))
				{
					result.EntityActions[builder.Id] = new EntityAction(null, buildAction, null, null);
				}
				else
				{
					result.EntityActions[builder.Id] = new EntityAction(new MoveAction(newBuilderPosition, true, false), buildAction, null, null);
				}

				LockBuildingPosition(buildingPosition, buildingProperties.Size);
				return true;
			}

			return false;
		}

		private void LockBuildingPosition(in Vec2Int buildingPosition, int size)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					_cells[yi][xi] = CellType.Building;
				}
			}
		}

		private double FindDistance(Vec2Int position1, Vec2Int position2)
		{
			double xDif = position1.X - position2.X;
			double yDif = position1.Y - position2.Y;
			return Math.Sqrt((xDif * xDif) + (yDif * yDif));
		}

		private bool CheckBuilderOnBuildingEdge(Vec2Int buildingPosition, Vec2Int builderPosition, int buildingSize)
		{
			int x1 = buildingPosition.X - 1;
			int x2 = buildingPosition.X + buildingSize;
			int y1 = buildingPosition.Y - 1;
			int y2 = buildingPosition.Y + buildingSize;

			if ((builderPosition.X > x1 && builderPosition.X < x2 && (builderPosition.Y == y1 || builderPosition.Y == y2))
				|| (builderPosition.Y > y1 && builderPosition.Y < y2 && (builderPosition.X == x1 || builderPosition.X == x2)))
			{
				return true;
			}

			return false;
		}

		private bool CheckFreeLocation(Vec2Int buildingPosition, int size, bool ignoreBuffer = false)
		{
			return CheckFreeLocation(buildingPosition.X, buildingPosition.Y, size, ignoreBuffer);
		}

		private bool CheckFreeLocation(int x, int y, int size, bool ignoreBuffer)
		{
			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					if (_cells[yi][xi] != CellType.Free)
					{
						return false;
					}
				}
			}

			int x1 = x - 1;
			int x2 = x + size;
			int y1 = y - 1;
			int y2 = y + size;

			if (x < 3 || y < 3 || ignoreBuffer)
			{
				if(x < 2 && y < 7)
				{
					for(int xi = x - 1; xi >= 0; xi--)
					{
						for(int yi = 0; yi < size; yi++)
						{
							if(_cells[y + yi][xi] is CellType.Army or CellType.Builder or CellType.Resource)
							{
								return false;
							}
						}
					}
				}
				else if(y < 2 && x < 7)
				{
					for (int yi = y - 1; yi >= 0; yi--)
					{
						for (int xi = 0; xi < size; xi++)
						{
							if (_cells[yi][x + xi] is CellType.Army or CellType.Builder)
							{
								return false;
							}
						}
					}
				}

				return true;
			}

			for (int i = 0; i < size; i++)
			{
				int xi = x + i;
				int yi = y + i;

				if (!(_cells[y2][xi] is CellType.Free /*or CellType.Builder or CellType.Army*/)
					|| !(_cells[y1][xi] is CellType.Free /*or CellType.Builder or CellType.Army*/)
					|| !(_cells[yi][x2] is CellType.Free /*or CellType.Builder or CellType.Army*/)
					|| !(_cells[yi][x1] is CellType.Free /*or CellType.Builder or CellType.Army*/))
				{
					return false;
				}
			}

			return true;
		}

		private bool TryGetFreeLocation(int size, out Vec2Int buildingPosition, out Vec2Int builderPosition, bool ignoreBuffer = false)
		{
			for (int num = 1; num < 50; num++)
			{
				for(int i = 0; i < num; i++)
				{
					int prevNum = num - 1;
					if (CheckFreeLocation(i, prevNum, size, ignoreBuffer))
					{
						buildingPosition = new Vec2Int(i, prevNum);
						if (TryGetFreeUnitLocation(buildingPosition, size, out builderPosition))
						{
							return true;
						}
					}

					if (CheckFreeLocation(prevNum, i, size, ignoreBuffer))
					{
						buildingPosition = new Vec2Int(prevNum, i);
						if (TryGetFreeUnitLocation(buildingPosition, size, out builderPosition))
						{
							return true;
						}
					}
				}
			}

			builderPosition = default;
			buildingPosition = default;
			return false;
		}

		private bool TryGetFreeUnitLocation(Vec2Int buildingPosition, int size, out Vec2Int unitPosition)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			int x1 = x - 1;
			int x2 = x + size;
			int y1 = y - 1;
			int y2 = y + size;

			for (int i = size - 1; i >= 0; i--)
			{
				int xi = x + i;
				int yi = y + i;

				if (_cells[yi][x2] is CellType.Free)
				{
					unitPosition = new Vec2Int(x2, yi);
					return true;
				}
				else if (_cells[y2][xi] is CellType.Free)
				{
					unitPosition = new Vec2Int(xi, y2);
					return true;
				}
				else if (y1 >= 0 && _cells[y1][xi] is CellType.Free)
				{
					unitPosition = new Vec2Int(xi, y1);
					return true;
				}
				else if (x1 >= 0 && _cells[yi][x1] is CellType.Free)
				{
					unitPosition = new Vec2Int(x1, yi);
					return true;
				}
			}

			unitPosition = default;
			return false;
		}

		private EntityProperties GetEntityProperties(EntityType entityType)
		{
			return entityType switch
			{
				EntityType.Wall => wallProperties,
				EntityType.House => houseProperties,
				EntityType.BuilderBase => builderBaseProperties,
				EntityType.BuilderUnit => builderUnitProperties,
				EntityType.MeleeBase => meleeBaseProperties,
				EntityType.MeleeUnit => meleeUnitProperties,
				EntityType.RangedBase => rangedBaseProperties,
				EntityType.RangedUnit => rangedUnitProperties,
				EntityType.Resource => resourceProperties,
				EntityType.Turret => turretProperties,
				_ => default,
			};
		}

		private void CollectEntities(PlayerView playerView)
		{
			void FillCells(int x, int y, int size, CellType cellType)
			{
				for (int xi = x; xi < x + size; xi++)
				{
					for (int yi = y; yi < y + size; yi++)
					{
						_cells[yi][xi] = cellType;
					}
				}
			}

			int entitiesLenght = playerView.Entities.Length;

			for (int i = 0; i < entitiesLenght; i++)
			{
				Entity entity = playerView.Entities[i];

				int x = entity.Position.X;
				int y = entity.Position.Y;

				if (entity.PlayerId == _selfInfo.Id)
				{
					myEntities.Add(entity);
					switch (entity.EntityType)
					{
						case EntityType.BuilderBase:
							builderBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, builderBaseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderUnit:
							builders.Add(entity);
							_freeBuilders.Add(entity.Id, entity);
							FillCells(x, y, builderUnitProperties.Size, CellType.Builder);
							break;
						case EntityType.RangedBase:
							rangedBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, rangedBaseProperties.Size, CellType.Building);
							break;
						case EntityType.RangedUnit:
							rangedUnits.Add(entity);
							FillCells(x, y, rangedUnitProperties.Size, CellType.Army);
							break;
						case EntityType.MeleeBase:
							meleeBases.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, meleeBaseProperties.Size, CellType.Building);
							break;
						case EntityType.MeleeUnit:
							meleeUnits.Add(entity);
							FillCells(x, y, meleeUnitProperties.Size, CellType.Army);
							break;
						case EntityType.House:
							houses.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, houseProperties.Size, CellType.Building);
							break;
						case EntityType.Turret:
							turrets.Add(entity);
							allBuildings.Add(entity);
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
					}
				}
				else if (entity.EntityType == EntityType.Resource)
				{
					resources.Add(entity);
					_cells[y][x] = CellType.Resource;
				}
				else
				{
					otherEntities.Add(entity);

					switch (entity.EntityType)
					{
						case EntityType.Wall:
							FillCells(x, y, wallProperties.Size, CellType.Building);
							break;
						case EntityType.House:
							FillCells(x, y, houseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderBase:
							FillCells(x, y, builderBaseProperties.Size, CellType.Building);
							break;
						case EntityType.BuilderUnit:
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.MeleeBase:
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
						case EntityType.MeleeUnit:
							_enemyTroops.Add(entity);
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.RangedBase:
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
						case EntityType.RangedUnit:
							_enemyTroops.Add(entity);
							_cells[y][x] = CellType.Enemy;
							break;
						case EntityType.Turret:
							_enemyTroops.Add(entity);
							FillCells(x, y, turretProperties.Size, CellType.Building);
							break;
					}

					if (x <= 40 && y <= 40)
					{
						if (x >= y)
							_rightNearEnemyCount++;
						else
							_leftNearEnemyCount++;
					}
					else if(x <= 50 && x > 40 && y <= 50 && y > 40)
					{
						if (x >= y)
							_rightFarEnemyCount++;
						else
							_leftFarEnemyCount++;
					}

					if (entity.Position.X <= MyPositionEdge && entity.Position.Y <= MyPositionEdge)
					{
						if (entity.Position.Y - entity.Position.X >= 0)
						{
							_leftEnemiesInMyBase.Add(entity);
						}
						else
						{
							_rightEnemiesInMyBase.Add(entity);
						}
					}
				}
			}
		}

		private void ClearLists()
		{
			myEntities.Clear();
			resources.Clear();
			otherEntities.Clear();
			_enemyTroops.Clear();

			_freeBuilders.Clear();
			builders.Clear();
			rangedUnits.Clear();
			meleeUnits.Clear();

			allBuildings.Clear();
			builderBases.Clear();
			rangedBases.Clear();
			meleeBases.Clear();
			houses.Clear();
			turrets.Clear();

			_leftEnemiesInMyBase.Clear();
			_rightEnemiesInMyBase.Clear();

			for (int i = 0; i < 80; i++)
			{
				for (int k = 0; k < 80; k++)
				{
					_cells[i][k] = CellType.Free;
				}
			}

			_leftNearEnemyCount = 0;
			_rightNearEnemyCount = 0;
			_leftFarEnemyCount = 0;
			_rightFarEnemyCount = 0;
	}

		private bool TryGetBuilding(int entityId, out Entity building)
		{
			building = allBuildings.FirstOrDefault(e => e.Id == entityId);

			return building.Id > 0;
		}

		private void BuildUnitAction(Entity building, EntityType buildingEntityType, EntityProperties entityProperties, in Action result, ref int lostResources)
		{
			int initialCost = entityProperties.InitialCost;

			if (_consumedFood + 1 <= _totalFood && TryGetFreeUnitLocation(building.Position, entityProperties.Size, out Vec2Int unitPosition))
			{
				lostResources -= initialCost;

				result.EntityActions[building.Id] = new EntityAction(null, new BuildAction(buildingEntityType, unitPosition), null, null);
			}
		}

		private bool TryGetParty(int entityid, out Party party)
		{
			if (_leftDefenseParty.Contains(entityid))
			{
				party = _leftDefenseParty;
				return true;
			}
			else if (_rightDefenseParty.Contains(entityid))
			{
				party = _rightDefenseParty;
				return true;
			}
			else if (_firstAttackParty.Contains(entityid))
			{
				party = _firstAttackParty;
				return true;
			}

			party = default;
			return false;
		}

		public void DebugUpdate(PlayerView playerView, DebugInterface debugInterface)
		{
			debugInterface.Send(new DebugCommand.Clear());
			debugInterface.GetState();
		}

		private string PrintMap()
		{
			StringBuilder sb = new StringBuilder(80 * 80 + 80);
			for (int x = 0; x < 80; x++)
			{
				for (int y = 0; y < 80; y++)
				{
					sb.Append(_cells[x][79 - y] == CellType.Free ? 1 : 0);
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}

		#region EntityTask

		private struct EntityTask
		{
			public int EntityId;

			public bool BuildingIsBuild;

			public bool Processed;

			public EntityAction EntityAction;

			public ActionType ActionType;

			public EntityTask(int entityId, bool buildingIsBuild, EntityAction entityAction, ActionType actionType)
			{
				EntityId = entityId;
				BuildingIsBuild = buildingIsBuild;
				Processed = true;
				EntityAction = entityAction;
				ActionType = actionType;
			}
		}

		private enum ActionType : byte
		{
			Build,
			Repair
		}

		#endregion

		#region Party

		private enum PartyType : byte
		{
			Defense,
			Attack,
			Hold
		}

		private enum DefensePosition : byte
		{
			Left,
			Right,
			Middle,
			Hold,
			None
		}

		private struct Party
		{
			private List<int> _party;
			private List<int> _partyTurned;

			public IReadOnlyList<int> Participants => _party;

			private int _capacity;
			public int Capacity => _capacity;

			private PartyType _partyType;
			public PartyType PartyType => _partyType;

			private Vec2Int _moveTo;
			public Vec2Int MoveTo => _moveTo;

			private DefensePosition _defensePosition;
			public DefensePosition DefensePosition => _defensePosition;

			public bool IsAttacking { get; set; }

			public Party(int capacity, PartyType partyType, Vec2Int moveTo, DefensePosition defensePosition)
			{
				_capacity = capacity;
				_party = new List<int>(capacity);
				_partyTurned = new List<int>(capacity);
				_partyType = partyType;
				_moveTo = moveTo;
				_defensePosition = defensePosition;
				IsAttacking = false;
			}

			public Party(int capacity, PartyType partyType) : this(capacity, partyType, default, DefensePosition.None) { }

			public void Add(int entityId)
			{
				//if(_party.Contains(entityId))
				//{
				//	return false;
				//}

				_party.Add(entityId);
				_partyTurned.Add(entityId);
				//return true;
			}

			public int Count => _party.Count;

			public bool Contains(int entityId) => _party.Contains(entityId);

			public void Turn(int entityId)
			{
				_partyTurned.Add(entityId);
			}

			public void Clear()
			{
				_party.Clear();
				foreach (int entityId in _partyTurned)
					_party.Add(entityId);

				_partyTurned.Clear();

				if (_party.Count == 0)
					IsAttacking = false;
			}
		}

		#endregion
	}
}