using Aicup2020.Model;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Action = Aicup2020.Model.Action;

namespace Aicup2020
{
	public class MyStrategy
	{
		private readonly EntityType[] _emptyEntityTypes = Array.Empty<EntityType>();
		private readonly EntityType[] _resourceEntityTypes = new EntityType[1] { EntityType.Resource };

		private Player _selfInfo;

		private int _lostResources;

		// Player info
		private readonly List<Entity> myEntities = new List<Entity>(10);
		private readonly List<Entity> builders = new List<Entity>(10);
		private readonly List<Entity> rangedUnits = new List<Entity>(10);
		private readonly List<Entity> meleeUnits = new List<Entity>(10);
		private readonly List<Entity> houses = new List<Entity>(10);
		private readonly List<Entity> builderBases = new List<Entity>(1);
		private readonly List<Entity> rangedBases = new List<Entity>(1);
		private readonly List<Entity> meleeBases = new List<Entity>(1);
		private readonly List<Entity> allBuildings = new List<Entity>(3);
		private readonly List<Entity> turrets = new List<Entity>(1);

		private readonly List<Entity> resources = new List<Entity>(1000);
		private readonly List<Entity> _enemyTroops = new List<Entity>(10);
		private readonly List<Entity> otherEntities = new List<Entity>(100);

		private readonly Dictionary<int, EntityAction> _entityActions = new Dictionary<int, EntityAction>(10);

		private readonly bool[][] _cells = new bool[80][];

		private int _countBuilderBasesInPrevTick = 0;
		private int _countRangeBasesInPrevTick = 0;
		private int _countMeleeBasesInPrevTick = 0;
		private int _countHousesInPrevTick = 0;

		private int _totalFood = 0;
		private int _consumedFood = 0;

		private Vec2Int _moveTo = new Vec2Int(0, 0);
		private int direction = 0;
		private bool destination = true;

		private Vec2Int _myBaseCenter = new Vec2Int(5, 5);

		private Vec2Int _leftFarWorkerPosition = new Vec2Int(5, 5);
		private Vec2Int _rightFarWorkerPosition = new Vec2Int(5, 5);

		private const float _buildersPercentage = 0.41f;
		private const float _armyPercentage = 1.0f - _buildersPercentage;
		
		private const int MyPositionEdge = 35;
		private const int MiddleRadius = 20;
		private const double enemyRadiusDetection = 6d;
		private List<Entity> _leftEnemiesInMyBase = new(2);
		private List<Entity> _rightEnemiesInMyBase = new(2);
		private List<Entity> _middleEnemiesInMyBase = new(2);
		private List<Entity> _threatOfBuilders = new(2);

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

		private bool onlyBuilders = false;

		private EntityAction? _entityRepairAction = null;
		private readonly SortedDictionary<int, EntityTask> entityTasks = new();
		private int turn = 0;

		private Party _leftDefenseParty = new(5, PartyType.Defense, new Vec2Int(20, 9), DefensePosition.Left);

		private Party _rightDefenseParty = new(5, PartyType.Defense, new Vec2Int(9, 20), DefensePosition.Right);

		private Party _middleDefenseParty = new(7, PartyType.Defense, new Vec2Int(20, 20), DefensePosition.Middle);

		private Party _firstAttackParty = new(15, PartyType.Attack, new Vec2Int(20, 20), DefensePosition.None);

		public MyStrategy()
		{
			for (int i = 0; i < 80; i++)
			{
				_cells[i] = new bool[80];
			}
		}

		private void UpdateAttackTarget()
		{
			if (!destination)
				return;

			destination = false;
			if (direction == 0)
			{
				_moveTo = new Vec2Int(78, 2);
				direction++;
			}
			else if (direction == 1)
			{
				_moveTo = new Vec2Int(2, 78);
				direction++;
			}
			else if (direction == 2)
			{
				_moveTo = new Vec2Int(78, 78);
				direction++;
			}
			else
			{
				if (otherEntities.Count > 0)
				{
					_moveTo = otherEntities[0].Position;
				}
				else
				{
					direction = 0;
				}
			}
		}

		private void ArmyUnitLogic(ref Entity unit, ref MoveAction? moveAction, in AttackAction? attackAction)
		{
			if(Math.Abs(FindDistance(unit.Position, _moveTo)) <= 5d)
			{
				destination = true;
			}

			if (TryGetParty(unit.Id, out Party party))
			{
				party.Turn(unit.Id);

				if (party.PartyType == PartyType.Defense)
				{					
					if(party.DefensePosition == DefensePosition.Left && LeftDefensePartyLogic(ref party, ref moveAction))
					{
						return;
					}

					if (party.DefensePosition == DefensePosition.Right && RightDefensePartyLogic(ref party, ref moveAction))
					{
						return;
					}

					if (party.DefensePosition == DefensePosition.Middle)
					{
						if (_threatOfBuilders.Count > 0)
						{
							moveAction = new MoveAction(_threatOfBuilders[0].Position, false, false);
							return;
						}

						if (MiddleDefensePartyLogic(ref party, ref moveAction))
							return;
					}

					moveAction = new MoveAction(party.MoveTo, false, false);
					return;
				}
				else if (party.PartyType == PartyType.Attack)
				{
					moveAction = new MoveAction(_moveTo, true, true);
					//entityTasks[unit.Id] = new EntityTask(unit.Id, false, new EntityAction(moveAction, null, attackAction, null), ActionType.Attack);
					return;

					if (entityTasks.TryGetValue(unit.Id, out EntityTask task))
					{
						if (!party.IsAttacking && unit.Position.X < MyPositionEdge && unit.Position.Y < MyPositionEdge)
						{
							moveAction = new MoveAction(party.MoveTo, false, false);
							entityTasks.Remove(unit.Id);
						}
						else
						{
							moveAction = task.EntityAction.MoveAction;
						}
					}

					if (party.IsAttacking)
					{
						moveAction = new MoveAction(_moveTo, true, true);
						entityTasks[unit.Id] = new EntityTask(unit.Id, false, new EntityAction(moveAction, null, attackAction, null), ActionType.Attack);
						return;
					}

					if(party.Count == party.Capacity)
					{
						party.IsAttacking = true;
						moveAction = new MoveAction(_moveTo, true, true);
						entityTasks[unit.Id] = new EntityTask(unit.Id, false, new EntityAction(moveAction, null, attackAction, null), ActionType.Attack);
						return;
					}

					moveAction = new MoveAction(party.MoveTo, false, false);
					return;
				}
			}

			Party? prt = null;

			if (_leftDefenseParty.Count < _leftDefenseParty.Capacity)
			{
				prt = _leftDefenseParty;
				_leftDefenseParty.Add(unit.Id);
				if(LeftDefensePartyLogic(ref party, ref moveAction))
				{
					return;
				}
			}
			else if (_rightDefenseParty.Count < _rightDefenseParty.Capacity)
			{
				prt = _rightDefenseParty;
				_rightDefenseParty.Add(unit.Id);
				if (RightDefensePartyLogic(ref party, ref moveAction))
				{
					return;
				}
			}
			else if (_middleDefenseParty.Count < _middleDefenseParty.Capacity)
			{
				prt = _middleDefenseParty;
				_middleDefenseParty.Add(unit.Id);
				if (MiddleDefensePartyLogic(ref party, ref moveAction))
				{
					return;
				}
			}

			if (prt.HasValue)
			{
				_firstAttackParty.IsAttacking = false;
				moveAction = new MoveAction(prt.Value.MoveTo, false, false);
				return;
			}

			_firstAttackParty.Add(unit.Id);

			// TODO: убрать дублирование
			//if (_firstAttackParty.IsAttacking)
			//{
			//	moveAction = new MoveAction(_moveTo, true, true);
			//	entityTasks[unit.Id] = new EntityTask(unit.Id, false, new EntityAction(moveAction, null, attackAction, null), ActionType.Attack);
			//	return;
			//}

			//if (_firstAttackParty.Count == _firstAttackParty.Capacity)
			//{
			//	_firstAttackParty.IsAttacking = true;
			//	moveAction = new MoveAction(_moveTo, true, true);
			//	entityTasks[unit.Id] = new EntityTask(unit.Id, false, new EntityAction(moveAction, null, attackAction, null), ActionType.Attack);
			//	return;
			//}

			moveAction = new MoveAction(_firstAttackParty.MoveTo, false, false);

			bool LeftDefensePartyLogic(ref Party party, ref MoveAction? moveAction)
			{
				if (_leftEnemiesInMyBase.Count > 0)
				{
					Entity enemy = _leftEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_rightEnemiesInMyBase.Count > 0 && _rightDefenseParty.Count == 0)
				{
					Entity enemy = _rightEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_middleEnemiesInMyBase.Count > 0 && _middleDefenseParty.Count == 0)
				{
					Entity enemy = _middleEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}

				return moveAction.HasValue;
			}

			bool RightDefensePartyLogic(ref Party party, ref MoveAction? moveAction)
			{
				if (_rightEnemiesInMyBase.Count > 0)
				{
					Entity enemy = _rightEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_leftEnemiesInMyBase.Count > 0 && _leftDefenseParty.Count == 0)
				{
					Entity enemy = _leftEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_middleEnemiesInMyBase.Count > 0 && _middleDefenseParty.Count == 0)
				{
					Entity enemy = _middleEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}

				return moveAction.HasValue;
			}

			bool MiddleDefensePartyLogic(ref Party party, ref MoveAction? moveAction)
			{
				if (_middleEnemiesInMyBase.Count > 0)
				{
					Entity enemy = _middleEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_leftEnemiesInMyBase.Count > 0 && _leftDefenseParty.Count == 0)
				{
					Entity enemy = _leftEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}
				else if (_rightEnemiesInMyBase.Count > 0 && _rightDefenseParty.Count == 0)
				{
					Entity enemy = _rightEnemiesInMyBase[0];
					moveAction = new MoveAction(enemy.Position, false, false);
				}

				return moveAction.HasValue;
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

			//Debug.WriteLine(PrintMap());

			UpdateAttackTarget();

			int buildersCount = builders.Count;

			_consumedFood = buildersCount + rangedUnits.Count + meleeUnits.Count;
			_totalFood = builderBases.Where(q => q.Active).Count() * builderBaseProperties.PopulationProvide
						+ rangedBases.Where(q => q.Active).Count() * rangedBaseProperties.PopulationProvide
						+ meleeBases.Where(q => q.Active).Count() * meleeBaseProperties.PopulationProvide
						+ houses.Where(q => q.Active).Count() * houseProperties.PopulationProvide;

			onlyBuilders = rangedBases.Count == 0 && buildersCount < 15 && _totalFood <= 20;

			IEnumerable<EntityAction> buildingTasks = entityTasks.Values.Where(q => q.ActionType == ActionType.Build).Select(q => q.EntityAction);
			int lostResources = _lostResources;

			foreach (EntityAction b in buildingTasks)
			{
				EntityProperties entityProperties = GetEntityProperties(b.BuildAction.Value.EntityType);

				lostResources -= entityProperties.InitialCost;
			}

			builders.ForEach(worker =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				EntityType[] validAutoAttackTargets = _emptyEntityTypes;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = GetEntityProperties(worker.EntityType);

				BuilderUnitLogic(ref moveAction, ref buildAction, ref repairAction, ref attackAction, ref entityAction, ref validAutoAttackTargets, in entityProperties, in worker, ref lostResources);

				result.EntityActions[worker.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			builderBases.ForEach(builderBase =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = GetEntityProperties(builderBase.EntityType);


				BuilderBaseLogic(ref playerView, ref builderBase, buildersCount, ref buildAction, ref entityProperties, ref lostResources);

				result.EntityActions[builderBase.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			if (!onlyBuilders)
			{
				rangedBases.ForEach(rangedBase =>
				{
					MoveAction? moveAction = null;
					BuildAction? buildAction = null;
					RepairAction? repairAction = null;
					AttackAction? attackAction = null;
					EntityAction? entityAction = null;

					EntityProperties entityProperties = GetEntityProperties(rangedBase.EntityType);

					EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
					EntityProperties unitEntityProperties = playerView.EntityProperties[buildingEntityType];

					BuildUnitAction(rangedBase.Position, buildingEntityType, entityProperties.Size, unitEntityProperties.InitialCost, ref buildAction, ref lostResources);
					result.EntityActions[rangedBase.Id] = entityAction ?? new EntityAction(
							moveAction,
							buildAction,
							attackAction,
							repairAction
						);
				});

				meleeBases.ForEach(meleeBase =>
				{
					MoveAction? moveAction = null;
					BuildAction? buildAction = null;
					RepairAction? repairAction = null;
					AttackAction? attackAction = null;
					EntityAction? entityAction = null;

					EntityProperties entityProperties = GetEntityProperties(meleeBase.EntityType);

					EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
					EntityProperties unitEntityProperties = playerView.EntityProperties[buildingEntityType];

					BuildUnitAction(meleeBase.Position, buildingEntityType, entityProperties.Size, unitEntityProperties.InitialCost, ref buildAction, ref lostResources);
					result.EntityActions[meleeBase.Id] = entityAction ?? new EntityAction(
							moveAction,
							buildAction,
							attackAction,
							repairAction
						);
				});
			}

			rangedUnits.ForEach(rangedUnit =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				EntityType[] validAutoAttackTargets = _emptyEntityTypes;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = GetEntityProperties(rangedUnit.EntityType);

				attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));

				ArmyUnitLogic(ref rangedUnit, ref moveAction, in attackAction);
				result.EntityActions[rangedUnit.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			meleeUnits.ForEach(meleeUnit =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				EntityType[] validAutoAttackTargets = _emptyEntityTypes;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = GetEntityProperties(meleeUnit.EntityType);

				attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));

				ArmyUnitLogic(ref meleeUnit, ref moveAction, in attackAction);
				result.EntityActions[meleeUnit.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			turrets.ForEach(turret =>
			{
				MoveAction? moveAction = null;
				BuildAction? buildAction = null;
				EntityType[] validAutoAttackTargets = _emptyEntityTypes;
				RepairAction? repairAction = null;
				AttackAction? attackAction = null;
				EntityAction? entityAction = null;

				EntityProperties entityProperties = GetEntityProperties(turret.EntityType);

				attackAction = new AttackAction(null, new AutoAttack(entityProperties.SightRange, validAutoAttackTargets));
				result.EntityActions[turret.Id] = entityAction ?? new EntityAction(
						moveAction,
						buildAction,
						attackAction,
						repairAction
					);
			});

			_countBuilderBasesInPrevTick = builderBases.Count;
			_countRangeBasesInPrevTick = rangedBases.Count;
			_countMeleeBasesInPrevTick = meleeBases.Count;
			_countHousesInPrevTick = houses.Count;

			EntityTask[] copy = entityTasks.Values.ToArray();
			foreach (EntityTask t in copy)
			{
				if (t.Processed == false)
				{
					entityTasks.Remove(t.EntityId);
				}
				else
				{
					entityTasks[t.EntityId] = new EntityTask(t.EntityId, t.BuildingIsBuild, t.EntityAction, t.ActionType);
				}
			}

			_leftDefenseParty.Clear();
			_rightDefenseParty.Clear();
			_middleDefenseParty.Clear();
			_firstAttackParty.Clear();

			return result;
		}

		private void BuilderBaseLogic(ref PlayerView playerView, ref Entity myEntity, int buildersCount, ref BuildAction? buildAction, ref EntityProperties entityProperties, ref int lostResources)
		{
			bool needBuilders = buildersCount < 40 
									&& (builders.Count < 10 || ((float)buildersCount / _totalFood) < _buildersPercentage
											|| !rangedBases.Where(q=>q.Active).Any());

			bool isSafely = _leftEnemiesInMyBase.Count == 0 || _rightEnemiesInMyBase.Count == 0 || _middleEnemiesInMyBase.Count == 0;

			EntityType buildingEntityType = entityProperties.Build.Value.Options[0];
			EntityProperties buildingEntityProperties = playerView.EntityProperties[buildingEntityType];

			if ((onlyBuilders || needBuilders) && isSafely)
			{
				BuildUnitAction(myEntity.Position, buildingEntityType, entityProperties.Size, buildingEntityProperties.InitialCost, ref buildAction, ref lostResources);
			}
		}

		private bool BuilderUnitRepairTaskLogic(ref EntityTask task, ref EntityAction eAction, ref EntityAction? entityAction, int myEntityId)
		{
			RepairAction rAction = eAction.RepairAction.Value;

			if (TryGetBuilding(rAction.Target, out Entity repairTarget))
			{
				if (repairTarget.EntityType == EntityType.House)
				{
					if (repairTarget.Health == houseProperties.MaxHealth)
					{
						entityTasks.Remove(myEntityId);
					}
					else
					{
						task.Processed = true;
						entityAction = eAction;
					}
				}
				else if (repairTarget.EntityType == EntityType.RangedBase)
				{
					if (repairTarget.Health == rangedBaseProperties.MaxHealth)
					{
						entityTasks.Remove(myEntityId);
						_entityRepairAction = null;
					}
					else
					{
						_entityRepairAction = eAction;
						entityAction = eAction;
						task.Processed = true;
					}
				}
				
				return true;
			}
			else
			{
				entityTasks.Remove(myEntityId);
			}

			return false;
		}

		private bool BuilderUnitBuildingTaskLogic(ref EntityTask task, ref EntityAction eAction, ref EntityAction? entityAction, int myEntityId)
		{
			BuildAction bAction = eAction.BuildAction.Value;
			EntityType neededBuildType = bAction.EntityType;
			EntityProperties bProperties = GetEntityProperties(neededBuildType);

			int neededBuildingsCountInPrevTick = neededBuildType switch
			{
				EntityType.BuilderBase => _countBuilderBasesInPrevTick,
				EntityType.RangedBase => _countRangeBasesInPrevTick,
				EntityType.MeleeBase => _countMeleeBasesInPrevTick,
				EntityType.House => _countHousesInPrevTick
			};

			List<Entity> entities = bAction.EntityType == EntityType.House ? houses : rangedBases;

			if(bAction.EntityType == EntityType.RangedBase)
			{
				Debug.WriteLine(bAction.EntityType);
			}

			if (entities.Count > 0)
			{
				EntityProperties buildingProperties = bAction.EntityType == EntityType.House ? houseProperties : rangedBaseProperties;

				IEnumerable<Entity> inactiveBuildings = entities.Where(q => q.Active == false && q.Position.X == bAction.Position.X && q.Position.Y == bAction.Position.Y && bAction.EntityType == q.EntityType);

				if(inactiveBuildings.Any())
				{
					entityTasks.Remove(myEntityId);

					Entity lastBuilding = inactiveBuildings.First();
					if (lastBuilding.Health < buildingProperties.MaxHealth)
					{
						//newRangedBaseBuilding = false;
						entityAction = new EntityAction(new MoveAction(new Vec2Int(lastBuilding.Position.X + 1, lastBuilding.Position.Y + 1), true, true), null, null, new RepairAction(lastBuilding.Id));
						entityTasks.Add(myEntityId, new EntityTask(myEntityId, true, entityAction.Value, ActionType.Repair));
						return true;
					}
				}
			}

			Vec2Int buildingPosition = bAction.Position;
			Vec2Int builderPosition = eAction.MoveAction.Value.Target;
			if (!CheckFreelocation(in buildingPosition, in builderPosition, bProperties.Size) && TryGetFreeLocation(bProperties.Size, out buildingPosition, out builderPosition))
			{
				LockBuildingPosition(in buildingPosition, in builderPosition, bProperties.Size);
				entityAction = new EntityAction(new MoveAction(builderPosition, true, true), new BuildAction(bAction.EntityType, buildingPosition), null, null);
			}
			else
			{
				LockBuildingPosition(in buildingPosition, in builderPosition, bProperties.Size);
				entityAction = eAction;
			}
			entityTasks[myEntityId] = new EntityTask(myEntityId, false, entityAction.Value, ActionType.Build);

			task.Processed = true;
			return true;
		}

		private void BuilderUnitLogic(ref MoveAction? moveAction, ref BuildAction? buildAction, ref RepairAction? repairAction, ref AttackAction? attackAction, ref EntityAction? entityAction, ref EntityType[] validAutoAttackTargets, in EntityProperties entityProperties, in Entity myEntity, ref int lostResources)
		{
			Vec2Int builderPosition = myEntity.Position;
			var threatsWorker = _enemyTroops.Where(q => Math.Abs(FindDistance(q.Position, builderPosition)) < enemyRadiusDetection);
			if (threatsWorker.Any())
			{
				moveAction = new MoveAction(_myBaseCenter, false, false);
				_threatOfBuilders.Add(threatsWorker.First());
				return;
			}

			if (entityTasks.TryGetValue(myEntity.Id, out EntityTask task))
			{
				EntityAction eAction = task.EntityAction;
				if (task.ActionType == ActionType.Repair && BuilderUnitRepairTaskLogic(ref task, ref eAction, ref entityAction, myEntity.Id))
				{
					return;
				}
				else if (task.ActionType == ActionType.Build && BuilderUnitBuildingTaskLogic(ref task, ref eAction, ref entityAction, myEntity.Id))
				{
					return;
				}
			}
			else if (_entityRepairAction.HasValue)
			{
				RepairAction rAction = _entityRepairAction.Value.RepairAction.Value;

				if (TryGetBuilding(rAction.Target, out Entity repairTarget) 
					&& repairTarget.EntityType == EntityType.RangedBase 
					&& entityTasks.Count(q => q.Value.ActionType == ActionType.Repair && q.Value.EntityAction.RepairAction.HasValue && q.Value.EntityAction.RepairAction.Value.Target == rAction.Target) < 5)
				{
					moveAction = new MoveAction(new Vec2Int(repairTarget.Position.X + 1, repairTarget.Position.Y + 1), true, true);

					if (repairTarget.Health == rangedBaseProperties.MaxHealth)
					{
						entityTasks.Remove(myEntity.Id);
						_entityRepairAction = null;
					}
					else
					{
						entityAction = new EntityAction(moveAction, buildAction, attackAction, rAction);

						entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Repair));
						return;
					}
				}
			}

			IEnumerable<BuildAction> buildingTasks = entityTasks.Where(q => q.Value.ActionType == ActionType.Build).Select(q => q.Value.EntityAction.BuildAction.Value);
			int rangedBasebuildingTasksCount = buildingTasks.Where(q => q.EntityType == EntityType.RangedBase).Count();
			int housebuildingTasksCount = buildingTasks.Where(q => q.EntityType == EntityType.House).Count();

			IEnumerable<RepairAction> repairingTasks = entityTasks.Where(q => q.Value.ActionType == ActionType.Repair).Select(q => q.Value.EntityAction.RepairAction.Value);

			int rangedBaseRepairingTasksCount = 0;
			int houseRepairingTasksCount = 0;

			foreach (RepairAction r in repairingTasks)
			{
				if (TryGetBuilding(r.Target, out Entity rapairTarget))
				{
					if (rapairTarget.EntityType == EntityType.House)
					{
						houseRepairingTasksCount++;
					}
					else if (rapairTarget.EntityType == EntityType.RangedBase)
					{
						rangedBaseRepairingTasksCount++;
					}
				}
			}

			if (rangedBases.Count == 0
				&& rangedBasebuildingTasksCount == 0
				&& rangedBaseRepairingTasksCount == 0
				&& !onlyBuilders
				&& lostResources >= rangedBaseProperties.InitialCost
				&& TryGetFreeLocation(rangedBaseProperties.Size, out Vec2Int buildingPositon, out Vec2Int rangedBaseBuilderPosition))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(rangedBaseBuilderPosition, true, true);
				buildAction = new BuildAction(EntityType.RangedBase, buildingPositon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
				entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Build));
				return;
			}

			int foodDeference = _totalFood - _consumedFood;

			bool needNewHouse = (_totalFood < 30 && foodDeference < 10 && (housebuildingTasksCount + houseRepairingTasksCount) < 3)
									|| (_totalFood >= 30 && rangedBases.Count > 0 && foodDeference < 10 && (housebuildingTasksCount + houseRepairingTasksCount) < 3)
									|| (_totalFood >= 30 && (rangedBases.Count == 0 && lostResources > 550 || rangedBases.Count == 0 && rangedBasebuildingTasksCount > 0 && lostResources >= 100) && foodDeference < 10 && (housebuildingTasksCount + houseRepairingTasksCount) < 3);

			//bool needNewHouse = (_totalFood > 50 
			//						&& foodDeference < 10
			//						&& (housebuildingTasksCount + houseRepairingTasksCount) < 2)
			//					|| (_totalFood <= 50 
			//						&& foodDeference < 5 
			//						&& housebuildingTasksCount < 1 
			//						&& houseRepairingTasksCount < 1);

			//bool needMoreHouse = (_totalFood < 30 && rangedBases.Count == 0) && ((_totalFood <= 40 && lostResources > 100 && ((housebuildingTasksCount + houseRepairingTasksCount) < 3))
			//						|| (foodDeference < 10 && _totalFood > 30 
			//							&& (lostResources > 150 
			//									&& ((housebuildingTasksCount + houseRepairingTasksCount) < 3))
			//								|| lostResources > 200 && ((housebuildingTasksCount + houseRepairingTasksCount) < 4))
			//						|| (foodDeference < 15 && _totalFood > 50 
			//							&& (lostResources > 200 
			//									&& ((housebuildingTasksCount + houseRepairingTasksCount) < 5))
			//								|| lostResources > 250 && ((housebuildingTasksCount + houseRepairingTasksCount) < 6))
			//						|| (foodDeference < 20 && _totalFood > 60 
			//							&& (lostResources > 250 
			//									&& ((housebuildingTasksCount + houseRepairingTasksCount) < 7))
			//								|| lostResources > 300
			//									&& ((housebuildingTasksCount + houseRepairingTasksCount) < 8)));

			if (needNewHouse && lostResources >= houseProperties.InitialCost && TryGetFreeLocation(houseProperties.Size, out Vec2Int positon, out Vec2Int houseBuilderPosition))
			{
				validAutoAttackTargets = _emptyEntityTypes;
				moveAction = new MoveAction(houseBuilderPosition, true, true);
				buildAction = new BuildAction(EntityType.House, positon);

				entityAction = new EntityAction(moveAction, buildAction, attackAction, repairAction);
				entityTasks.Add(myEntity.Id, new EntityTask(myEntity.Id, true, entityAction.Value, ActionType.Build));
				return;
			}

			validAutoAttackTargets = _resourceEntityTypes;
			attackAction = new AttackAction(null, new AutoAttack(80, validAutoAttackTargets));
		}

		private void LockBuildingPosition(in Vec2Int buildingPosition, in Vec2Int builderPositon, int size)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					_cells[yi][xi] = true;
				}
			}

			_cells[builderPositon.Y][builderPositon.X] = true;
		}

		private bool TryGetFreeUnitLocation(in Vec2Int buildingPosition, int size, out Vec2Int unitPosition)
		{
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			int x1 = x - 1;
			int x2 = x + size;
			int y1 = y - 1;
			int y2 = y + size;


			for (int i = 0; i < size; i++)
			{
				int xi = x + i;
				int yi = y + i;

				if (!_cells[y1][xi])
				{
					unitPosition = new Vec2Int(xi, y1);
					return true;
				}
				else if (!_cells[y2][xi])
				{
					unitPosition = new Vec2Int(xi, y2);
					return true;
				}
				else if (!_cells[yi][x1])
				{
					unitPosition = new Vec2Int(x1, yi);
					return true;
				}
				else if (!_cells[yi][x2])
				{
					unitPosition = new Vec2Int(x2, yi);
					return true;
				}
			}

			unitPosition = default;
			return false;
		}

		private bool CheckFreelocation(in Vec2Int buildingPosition, in Vec2Int builderPositon, int size)
		{
			bool notFree = false;
			int x = buildingPosition.X;
			int y = buildingPosition.Y;

			for (int xi = x; xi < x + size; xi++)
			{
				for (int yi = y; yi < y + size; yi++)
				{
					if (_cells[yi][xi])
					{
						notFree = true;
						break;
					}
				}

				if (notFree)
				{
					break;
				}
			}

			if (!notFree)
			{
				bool builderPositionFound = false;

				if(_cells[builderPositon.Y][builderPositon.X])
				{
					builderPositionFound = true;
				}

				if (builderPositionFound)
				{
					return true;
				}
			}

			return false;
		}

		private bool TryGetFreeLocation(int size, out Vec2Int buildingPosition, out Vec2Int builderPosition)
		{
			for (int num = 1; num < 40; num++)
			{
				for (int x = 1; x < num; x++)
				{
					for(int y = 1; y < num; y++)
					{
						bool isFree = true;
						for (int xi = x; xi < x + size; xi++)
						{
							for (int yi = y; yi < y + size; yi++)
							{
								if (_cells[yi][xi])
								{
									isFree = false;
									break;
								}
							}

							if (!isFree)
							{
								break;
							}
						}

						if (isFree)
						{
							buildingPosition = new Vec2Int(x, y);

							if (TryGetFreeUnitLocation(in buildingPosition, size, out builderPosition))
								return true;
						}

					}
				}
				//for (int y = 1; y < num; y++)
				//{
				//	bool isFree = false;
				//	int x = num;
				//	for (int xi = x; xi <= x + size; xi++)
				//	{
				//		for (int yi = y; yi <= y + size; yi++)
				//		{
				//			if (_cells[yi][xi])
				//			{
				//				isFree = false;
				//				break;
				//			}
				//		}

				//		if (!isFree)
				//		{
				//			break;
				//		}
				//	}

				//	if (isFree)
				//	{
				//		buildingPosition = new Vec2Int(x, y);

				//		if (TryGetFreeUnitLocation(in buildingPosition, size, out builderPosition))
				//			return true;
				//	}
				//}
			}

			builderPosition = default;
			buildingPosition = default;
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
			int entitiesLenght = playerView.Entities.Length;

			//otherArmyInMyBase = null;

			for (int i = 0; i < entitiesLenght; i++)
			{
				Entity entity = playerView.Entities[i];
				EntityProperties entityProperties = GetEntityProperties(entity.EntityType);

				int x = entity.Position.X;
				int y = entity.Position.Y;

				for (int xi = x; xi < x + entityProperties.Size; xi++)
				{
					for (int yi = y; yi < y + entityProperties.Size; yi++)
					{
						_cells[yi][xi] = true;
					}
				}

				if (entity.PlayerId == _selfInfo.Id)
				{
					myEntities.Add(entity);
					switch (entity.EntityType)
					{
						case EntityType.BuilderBase:
							builderBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.BuilderUnit:
							builders.Add(entity);
							break;
						case EntityType.RangedBase:
							rangedBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.RangedUnit:
							rangedUnits.Add(entity);
							break;
						case EntityType.MeleeBase:
							meleeBases.Add(entity);
							allBuildings.Add(entity);
							break;
						case EntityType.MeleeUnit:
							meleeUnits.Add(entity);
							break;
						case EntityType.House:
							houses.Add(entity);
							allBuildings.Add(entity);
							break;
					}
				}
				else if (entity.EntityType == EntityType.Resource)
				{
					resources.Add(entity);
				}
				else
				{
					otherEntities.Add(entity);

					if(entity.EntityType is EntityType.RangedUnit or EntityType.MeleeUnit or EntityType.Turret)
					{
						_enemyTroops.Add(entity);
					}

					if (entity.Position.X <= MyPositionEdge && entity.Position.Y <= MyPositionEdge)
					{
						if(entity.Position.Y - entity.Position.X >= 0)
						{
							_leftEnemiesInMyBase.Add(entity);
						}
						else
						{
							_rightEnemiesInMyBase.Add(entity);
						}

						if(entity.Position.Y > MyPositionEdge - MiddleRadius
							&& entity.Position.Y < MyPositionEdge
							&& entity.Position.X > MyPositionEdge - MiddleRadius
							&& entity.Position.X < MyPositionEdge)
						{
							_middleEnemiesInMyBase.Add(entity);
						}

						//if (otherArmyInMyBase.HasValue)
						//{
						//	if (FindDistance(entity.Position) < FindDistance(otherArmyInMyBase.Value.Position))
						//	{
						//		otherArmyInMyBase = entity;
						//	}
						//}
						//else
						//{
						//	otherArmyInMyBase = entity;
						//}
					}
				}
			}
		}

		private double FindDistance(Vec2Int entityPosition) => FindDistance(entityPosition, _myBaseCenter);

		private double FindDistance(Vec2Int position1, Vec2Int position2)
		{
			double xDif = position1.X - position2.X;
			double yDif = position1.Y - position2.Y;
			return Math.Sqrt((xDif * xDif) + (yDif * yDif));
		}

		private void ClearLists()
		{
			resources.Clear();

			// Units
			myEntities.Clear();
			builders.Clear();
			rangedUnits.Clear();
			meleeUnits.Clear();

			// Buildings
			allBuildings.Clear();
			builderBases.Clear();
			rangedBases.Clear();
			meleeBases.Clear();
			houses.Clear();
			turrets.Clear();

			// Enemies
			otherEntities.Clear();
			_leftEnemiesInMyBase.Clear();
			_rightEnemiesInMyBase.Clear();
			_middleEnemiesInMyBase.Clear();
			_enemyTroops.Clear();

			for (int i = 0; i < 80; i++)
			{
				for (int k = 0; k < 80; k++)
				{
					_cells[i][k] = false;
				}
			}
		}

		private bool TryGetBuilding(int entityId, out Entity building)
		{
			building = allBuildings.FirstOrDefault(e => e.Id == entityId);

			return building.Id > 0;
		}

		private bool TryGetParty(int entityid, out Party party)
		{
			if(_leftDefenseParty.Contains(entityid))
			{
				party = _leftDefenseParty;
				return true;
			}
			else if (_rightDefenseParty.Contains(entityid))
			{
				party = _rightDefenseParty;
				return true;
			}
			else if (_middleDefenseParty.Contains(entityid))
			{
				party = _middleDefenseParty;
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

		private void BuildUnitAction(Vec2Int buildingPosition, EntityType buildingEntityType, int size, int initialCost, ref BuildAction? buildAction, ref int lostResources)
		{
			if (_consumedFood + 1 <= _totalFood && lostResources >= initialCost && TryGetFreeUnitLocation(in buildingPosition, size, out Vec2Int unitPosition))
			{
				lostResources -= initialCost;

				buildAction =  new BuildAction(buildingEntityType, unitPosition);
			}
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
					sb.Append(_cells[x][79 - y] ? 1 : 0);
				}

				sb.AppendLine();
			}

			return sb.ToString();
		}
	}

	public struct EntityTask
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

	public enum ActionType : byte
	{
		Build,
		Repair,
		Attack
	}

	public enum PartyType : byte
	{
		Defense,
		Attack,
		Hold
	}

	public enum DefensePosition : byte
	{
		Left,
		Right,
		Middle,
		Hold,
		None
	}

	public struct Party
	{
		private List<int> _party;
		private List<int> _partyTurned;

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
}