using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEditor;
/// <summary>
/// 鸟群（粒子实体控制）
/// BSA鸟群算法+Dots性能优化
/// </summary>
public class Boids : MonoBehaviour
{
    private static Boids _instance;
    
    public static Boids Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<Boids>();
            }
            return _instance;
        }
        //set => _instance = value;
    }

    [Header("队伍中的粒子（对象）数量")]
    public int boidsPerTeam = 1024;
    int maxBoids;//内部使用的总上限。即 boidsPerTeam * teams.Length
    public ParticleSystem system;//粒子系统
    private ParticleSystemRenderer systemRenderer;//粒子渲染引用
    public ParticleSystem explosionSystem;//爆炸粒子效果粒子
    public ParticleSystem.EmitParams explosionSystem_ep;//控制爆炸粒子的发射参数（如位置、速度等）
    public ParticleSystem laserSystem;//激光 的粒子系统，类似于 Boid 发射激光的效果。
    public ParticleSystem.EmitParams laserSystem_ep;//激光参数

    public float spawnRate = 0.3f;//生成频率
    private float spawnTicker = 1.0f;    //生成冷却计时

    public Team[] teams;//总共有多少个队伍
    
    [Header("单对象设置：速度")]
    public float boidSpeed = 10f;
    [Header("单对象设置：速度变化允许范围")]
    public float boidSpeedVariation = 2f;
    [Header("单对象设置：转向速度")]
    public float boidTurnSpeed = 0.5f;
    [Header("单对象设置：转向速度变化允许范围")]
    public float boidTurnSpeedVariation = 0.1f;

    public Transform randomBoid;
    private Boid nullBoid;//空的 Boid 实例，用于处理找不到的情况。
    private int boidIndex = -1;//用于存储当前随机选择的 Boid 的索引。
    private int boidSpawnIndex;//用于记录下一个 Boid 的生成id。

    //=======================dots
    private NativeArray<Boid> _boids;
    [NonSerialized] public NativeArray<Boid> _boidsAlt;
    //存储每个粒子的数组，表示每个 Boid 对应的粒子。
    private NativeArray<ParticleSystem.Particle> _particles;

    //JobHandle 用来处理 Boid 的运动任务
    private JobHandle _boidMovementHandle;
    //用来处理 Boid 数据的复制任务
    private JobHandle _boidCopyHandle;


    //记录每个 Boid 受到的伤害，用于在 Boid 死亡时生成爆炸效果。
    private float[] DamageList;

    // TODO 应该做成一个状态机
    private bool startFrame = true;//表示当前是否是第一帧，用于初始化一些处理。
    private bool resetNextFrame = false;//是否需要在下一帧重置所有 Boid 的状态。




    /// <summary>
    /// 激活初始化
    /// </summary>
    private void OnEnable()
    {
        nullBoid = new Boid
        {
            active = false,
            id = -1
        };

        maxBoids = boidsPerTeam * teams.Length;
        
        system.AllocateMeshIndexAttribute();
        var main = system.main;
        main.maxParticles = maxBoids;
        
        _particles = new NativeArray<ParticleSystem.Particle>(maxBoids, Allocator.Persistent);
        
        system.GetParticles(_particles);
        _boids = new NativeArray<Boid>(maxBoids, Allocator.Persistent);
        DamageList = new float[_boids.Length];

        systemRenderer = system.GetComponent<ParticleSystemRenderer>();

        var meshes = new Mesh[teams.Length];
        for (int t = 0; t < teams.Length; t++)
        {
            meshes[t] = teams[t].ship.mesh;
            
            for (int i = 0; i < boidsPerTeam; i++)
            {
                var index = t * boidsPerTeam + i;
                
                // setup boids
                var boid = _boids[index];
                boid.Init(index % teams.Length, index);
                
                boid.ResetBoid();
                _boids[index] = boid;
                
                // setup particles
                var p = _particles[index];
                p.SetMeshIndex((int)boid.team);
                p.startLifetime = 6f;
                p.startColor = teams[boid.team].laserColor;
                _particles[index] = p;
            }
        }
        systemRenderer.SetMeshes(meshes);

        system.SetParticles(_particles);

        _boidsAlt = new NativeArray<Boid>(maxBoids, Allocator.Persistent);
    }
    
    private void OnDisable()
    {
        _boidCopyHandle.Complete();
        
        if (_boids.IsCreated)
            _boids.Dispose();
        if (_particles.IsCreated)
            _particles.Dispose();
        if (_boidsAlt.IsCreated)
            _boidsAlt.Dispose();
    }
    /// <summary>
    /// 控制Boids的生成、激活状态、爆炸和激光效果。
    /// 更新每个Boid的位置，并根据随机Boid选择来更新randomBoid
    /// 粒子的更新，确保粒子系统展示当前Boid的状态。
    /// </summary>
    private void Update()
    {
        SpawnTicker();//控制生成间隔

        if (startFrame)return;// 如果是游戏开始的帧，则不执行任何逻辑

        _boidCopyHandle.Complete();//等待上一帧LateUpdate的复制

        //重置信号
        if (resetNextFrame)
        {
            for (var index = 0; index < _boids.Length; index++)
            {
                var boid = _boids[index];
                boid.ResetBoid();
                _boids[index] = boid;
            }
            boidSpawnIndex = 0;
            // reset damage list
            for (int i = 0; i < DamageList.Length; i++)
            {
                DamageList[i] = 0;
            }
            resetNextFrame = false;
        }
        
        SpawnNewShips();
        SpawnExplosions();
        SpawnLasers();

        //随机选一个对象作为现在的焦点
        if (randomBoid)
        {
            if (boidIndex >= 0 && _boids[boidIndex].active && _boids[boidIndex].team == 0)
            {
                randomBoid.forward = _boids[boidIndex].velocity;
                randomBoid.position = _boids[boidIndex].position;// Vector3.Lerp(randomBoid.position, _boids[boidIndex].position, Time.deltaTime);
            }
            else
            {
                boidIndex = UnityEngine.Random.Range(0, _boids.Length);
            }
        }

        system.SetParticles(_particles);//控制粒子系统
        _boidsAlt.CopyFrom(_boids);
        // read frames
    }
    /// <summary>
    /// 作用：更新Boid的运动和粒子数据。
    /// 通过Job System异步处理Boid的运动（BoidMovementJob）和粒子位置复制（BoidCopyJob）。
    /// 控制Boids的运动、转向和状态更新，并平滑其速度和位置。
    /// </summary>
    private void LateUpdate()
    {

        //粒子对象的运动计算
        var movement = new BoidMovementJob()
        {
            _boids = _boids,
            _boidsAlt = _boidsAlt,
            deltaTime = Time.deltaTime,
            playerBoid = boidIndex,
        };

        var handle = new JobHandle();
        _boidMovementHandle = movement.Schedule(_boids.Length, handle);
        
        var copy = new BoidCopyJob()
        {
            _particles = _particles,
            _boids = _boids,
            playerBoid = boidIndex,
        };

        _boidCopyHandle = copy.Schedule(_boids.Length, 32, _boidMovementHandle);
        
        JobHandle.ScheduleBatchedJobs();

        //记录游戏开始的第一帧到此结束
        if (startFrame)
            startFrame = false;
    }
    /// <summary>
    /// 以对象id获取对象
    /// </summary>
    public Boid GetBoid(int id)
    {
        foreach (var boid in _boidsAlt)
        {
            if (boid.id == id)
            {
                return boid;
            }
        }

        return nullBoid;
    }

    /// <summary>
    /// 找到最近的
    /// </summary>
    /// <param name="positionWS">坐标</param>
    public Boid GetClosestBoid(Vector3 positionWS)
    {
        return GetClosestBoid(positionWS, -1);
    }
    /// <summary>
    /// 找到最近的
    /// </summary>
    /// <param name="positionWS">坐标</param>
    /// <param name="team">队伍序号</param>
    public Boid GetClosestBoid(Vector3 positionWS, int team)
    {
        var i = -1;
        var dist = float.PositiveInfinity;
        for (var index = 0; index < _boidsAlt.Length; index++)
        {
            var boid = _boidsAlt[index];
            if(!boid.active || boid.health <= 0 || team == boid.team) continue;
            var boidDist = Vector3.Distance(boid.position, positionWS);
            if (!(boidDist < dist)) continue;
            dist = boidDist;
            i = index;
        }

        if (i >= 0 && i < _boidsAlt.Length)
        {
            return _boidsAlt[i];
        }
        else
        {
            return nullBoid;
        }
    }

    public void ResetBoids()
    {
        resetNextFrame = true;
    }

    public void DamageBoid(int id, float damage)
    {
        if(DamageList.Length > id && id >= 0)
        {
            DamageList[id] += damage;
        }
    }
    //单线程顺序执行
    [BurstCompile]
    struct BoidMovementJob : IJobFor
    {
        public NativeArray<Boid> _boids;//当前对象状态
        [ReadOnly]
        public NativeArray<Boid> _boidsAlt;//上一帧状态

        [ReadOnly] public float deltaTime;

        [ReadOnly] public int playerBoid;//玩家自己的位置信息

        public void Execute(int index)
        {
            var b = _boids[index];  
            // 在每个线程中初始化一个随机数生成器
            Unity.Mathematics.Random random = new Unity.Mathematics.Random((uint)(1+ index)); // 使用线程索引作为种子,种子不能是0


            if (!b.active) return;

            float turningSpeed = b.turningSpeed;

            // 初始化目标方向和目标面向角度
            float3 targetDir;
            float targetFacing = 0f;
            //开火冷却时间
            b.rateOfFireTemp -= deltaTime;

            if (b.targetID == -1 || _boidsAlt[b.targetID].active == false)
            {
                // 如果 Boid 没有目标，或者目标已经死亡，则重新设定目标方向
                b.targetID = -1;
                b.targetAge = 0;
                targetDir = math.normalize(b.destination - b.position);
            }
            else
            {
                // 如果 Boid 有目标，计算目标方向和与目标朝向的夹角
                targetDir = math.normalize(_boidsAlt[b.targetID].position - b.position);
                //点乘
                //如果两个向量完全对齐（即夹角为 0°），则点积的值为 1。
                //如果两个向量完全相反（即夹角为 180°），则点积的值为 - 1。
                //如果两个向量垂直（即夹角为 90°），则点积的值为 0。
                targetFacing = math.dot(targetDir, math.normalize(b.velocity));
                targetDir *= 2f;//让目标对 Boid 的运动产生更强的影响
            }

            // 初始化 Boid算法 的三个行为向量：凝聚力、分离力和对齐力
            float3 cohesionVect = 0;
            float3 separationVect = 0;
            float3 alignmentVect = 0;

            // 用于计算凝聚力、分离力的计数器
            int separationCount = 0;
            int cohesionCount = 0;

            for(int i = 0; i < _boidsAlt.Length; i++)
            {
                if(b.id != _boidsAlt[i].id && _boidsAlt[i].active)
                {
                    // 计算与其他 Boid 的距离
                    float3 distVect = _boidsAlt[i].position - b.position;
                    float dist = math.length(distVect);

                    if (b.team == _boidsAlt[i].team)
                    {
                        // 同队的 Boid，计算凝聚力和对齐力 
                        // 如果 Boid 之间距离较近
                        if (dist < 100f)
                        {
                            cohesionVect += distVect * 0.1f;  // 增加凝聚力向量
                            cohesionCount++;
                            alignmentVect += _boidsAlt[i].velocity;  // 增加对齐力向量
                        }
                    }
                    else
                    {
                        // 敌队的 Boid，计算攻击目标和分离力
                        if (dist < 350f)
                        {
                            // 如果没有目标，把这个目标当作自己的攻击目标。
                            // 如果与非玩家战斗时间过长还没分出胜负，跳出逻辑
                            if ((b.targetID == -1 || b.targetAge >= 5000f) && i != playerBoid)
                            {
                                if (random.NextFloat(0, 1f) > 0.97f) continue;//让大家选择不同的敌人
                                b.targetID = i;
                                b.targetAge = 0;
                            }
                            else
                            {
                                if (i == b.targetID)
                                {
                                    var targetBoid = _boids[i];
                                    //如果敌方已死，重置
                                    if (!targetBoid.active)
                                    {
                                        b.targetID = -1;
                                        b.targetAge = 0;
                                        continue;
                                    }

                                    turningSpeed *= 2;//狗斗

                                    // 如果目标方向与当前速度方向对齐且开火冷却结束，则攻击目标,对齐度越大表示需要在越正面
                                    if (targetFacing > 0.5f && b.rateOfFireTemp <= 0)
                                    {
                                        targetBoid.health -= b.damage;
                                        b.shooting = true;
                                        b.rateOfFireTemp = b.rateOfFire;
                                    }
                                    //击杀目标
                                    if (targetBoid.health <= 0f)
                                    {
                                        targetBoid.active = false;
                                    }

                                    _boids[i] = targetBoid;
                                }
                            }
                        }
                    }
                    // 分离力，避免 Boid 过于靠近
                    if (dist <= 50f && dist > 0){
                        separationVect -= distVect / (dist * 0.1f);
                        separationCount++;
                    }
                }
            }

            // 计算最终的凝聚力、对齐力和分离力
            if (cohesionCount > 0)
            {
                cohesionVect /= cohesionCount;
                alignmentVect /= cohesionCount;
            }

            if(separationCount > 0)
                separationVect /= separationCount;


            // 将所有力合并，计算新的速度
            var vec = float3.zero;
            vec += cohesionVect * 0.01f;  // 凝聚力
            vec += separationVect * 0.5f;  // 分离力
            vec += alignmentVect * 0.1f;  // 对齐力
            vec += targetDir;  // 目标方向
            vec += b.velocity;  // 当前速度

            // 规范化速度
            var vel = math.normalizesafe(vec + math.EPSILON);

            // 限制最大旋转角度，避免速度突变
            vel = RotateTowards(b.velocity, vel, math.radians(100f * deltaTime), 0f);

            // 平滑速度
            b.velocity = math.lerp(b.velocity, math.lerp(b.smoothVel, vel, 0.5f), 0.5f);

            // 更新 Boid 运动后的位置
            b.position += b.velocity * b.speed * deltaTime; 

            // 平滑
            b.smoothVel = math.lerp(b.smoothVel, vel, 0.1f);

            // 更新目标已经被瞄准的时间
            b.targetAge++;
            _boids[index] = b;
        }

        /// <summary>
        /// 旋转当前速度朝向目标方向
        /// </summary>
        /// <param name="start">原来的速度向量</param>
        /// <param name="end">期望的速度向量</param>
        /// <param name="maxAngle">最大旋转角度（后续计算是弧度）</param>
        /// <param name="maxMagnitude">转速的加速度上限</param>
        /// <returns></returns>
        float3 RotateTowards(float3 start, float3 end, float maxAngle, float maxMagnitude)
        {
            var startMag = math.length(start);
            var endMag = math.length(end);

            var dot = math.dot(start, end);

            // 如果方向几乎是浮点数误差，则直接返回目标方向
            if (dot > 1f - math.EPSILON)
            {
                return end;
            }
            else
            {
                // 计算旋转
                var angle = math.acos(dot);
                var axis = math.normalize(math.cross(start, end));
                var matrix = float3x3.AxisAngle(axis, math.min(angle, maxAngle));
                float3 rotated = math.mul(matrix, start);
                rotated *= ClampedMove(startMag, endMag, maxMagnitude);
                return math.normalize(rotated);
            }
        }

        /// <summary>
        /// 限制速度变化
        /// </summary>
        /// <param name="start">原来的速度</param>
        /// <param name="end">期望的速度</param>
        /// <param name="clampedDelta">加速度上限</param>
        /// <returns></returns>
        static float ClampedMove(float start, float end, float clampedDelta)
        {
            var delta = end - start;
            if (delta > 0.0F)
                return start + math.min(delta, clampedDelta);
            else
                return start - math.min(-delta, clampedDelta);
        }
        
        static float3 Slerp(float3 start, float3 end, float percent)
        {
            float dotP = math.dot(start, end);
            dotP = math.clamp(dotP, -1.0f, 1.0f);
            float theta = math.acos(dotP)*percent;
            float3 RelativeVec = math.normalizesafe(end - start*dotP);
            return ((start*math.cos(theta)) + (RelativeVec*math.sin(theta)));
        }
    }

    /// <summary>
    /// 将数据通知粒子系统
    /// IJobParallelFor多线程无序执行
    /// </summary>
    [BurstCompile]
    struct BoidCopyJob : IJobParallelFor
    {
        public NativeArray<ParticleSystem.Particle> _particles;
        [ReadOnly]
        public NativeArray<Boid> _boids;

        [ReadOnly] public int playerBoid;

        public void Execute(int index)
        {
            var p = _particles[index];
            var b = _boids[index];

            if (b.active)
            {
                //setup alive boid
                p.velocity =  math.normalizesafe(b.position - (float3)p.position);
                p.position = b.position;
                p.remainingLifetime = math.clamp(p.remainingLifetime + 1f, 0.1f, 10f);
                p.rotation = 0f;
                p.startSize3D = index != playerBoid ? Vector3.one : Vector3.zero;
                p.startSize = math.clamp(p.startSize + 0.1f, 0.0f, 1f);
            }
            else
            {
                //null boid
                p.position = Vector3.zero;
                p.remainingLifetime = -10f;
                p.rotation += 0.1f;
                p.startSize3D = Vector3.zero;
                p.startSize = 0f;
            }
            
            _particles[index] = p;
        }
    }
    /// <summary>
    /// 生成激光武器的射线效果
    /// </summary>
    private void SpawnLasers()
    {
        // do the lasers
        for (int i = 0; i < _boids.Length; i++)
        {
            var b = _boids[i];

            if (b.shooting && b.targetID != -1 && _boids[b.targetID].active)
            {
                laserSystem_ep.position = b.position;

                var vector = _boids[b.targetID].position - b.position;
                var distance = math.length(vector);
                var dir = math.normalize(vector);
                laserSystem_ep.velocity = dir * 1200f;
                laserSystem_ep.startLifetime = distance / 1200f;
                laserSystem_ep.startColor = teams[b.team].laserColor;
                laserSystem.Emit(laserSystem_ep, 1);
                b.shooting = false;
            }

            _boids[i] = b;
        }
    }
    /// <summary>
    /// 在每个死亡对象位置生成爆炸效果
    /// </summary>
    private void SpawnExplosions()
    {
        for (var i = 0; i < _boids.Length; i++)
        {
            var boid = _boids[i];

            boid.health -= DamageList[boid.id];
            DamageList[boid.id] = 0f;
            
            if (boid.health <= 0f)
            {
                explosionSystem_ep.position = boid.position;
                explosionSystem_ep.velocity = boid.velocity * 0.1f;
                explosionSystem.Emit(explosionSystem_ep, 1);
                boid.health = 100f;
                boid.position = teams[boid.team].spawnPoints[UnityEngine.Random.Range(0, teams[boid.team].spawnPoints.Length)].position;
            }
            _boids[i] = boid;
        }
    }
    /// <summary>
    /// 生成新粒子对象
    /// </summary>
    private void SpawnNewShips()
    {
        if (spawnTicker > 0f) return;

        while (_boids[boidSpawnIndex].active)
        {
            boidSpawnIndex = (int)Mathf.Repeat(boidSpawnIndex + 1, _boids.Length);
        }
        
        var boid = _boids[boidSpawnIndex];

        var point = teams[boid.team].spawnPoints[UnityEngine.Random.Range(0, teams[boid.team].spawnPoints.Length)];

        if (point.gameObject.activeInHierarchy)
        {
            boid.Spawn(point.position, point.forward);
            _boids[boidSpawnIndex] = boid;
            boidSpawnIndex = 0;
        }

        ResetTicker();
    }

    #region 控制生成速度
    private void SpawnTicker()
    {
        if (spawnTicker > 0f)
            spawnTicker -= Time.deltaTime;
        else
        {
            ResetTicker();
        }
    }
    private void ResetTicker()
    {
        spawnTicker = spawnRate;
    }
    #endregion 控制生成速度

    public struct Boid
    {
        // states
        public bool active;//是否活跃
        public bool shooting;//是否正在射击
        //自身属性
        public float health;//血量
        public float damage;//伤害
        public float rateOfFire;//攻击冷却时间
        public float speed;//飞行速度
        public float turningSpeed;//转向速度
        //运动数据
        public float3 position;
        public float3 velocity;//速度向量
        public float3 smoothVel;//插值向量
        public float3 destination;//目的地
        public float rateOfFireTemp;//当前攻击冷却时间
        //攻击目标数据
        public int targetID;
        public int targetAge;//认为和这个目标已经战斗了多久
        //阵营参数
        public int team;//所属阵营
        public int id;
        
        public void Init(int team, int id)
        {
            position = float3.zero;
            velocity = float3.zero;
            smoothVel = float3.zero;
            destination = float3.zero;
            active = false;
            shooting = false;
            health = 100f;
            damage = 3f;
            rateOfFire = 0.5f;
            speed = 10f;
            turningSpeed = 50f;
            targetID = -1;
            targetAge = 1;
            this.team = team;
            this.id = id;
        }
        /// <summary>
        /// 初始化
        /// </summary>
        public void ResetBoid()
        {
            Init(team, id);
        }
        /// <summary>
        /// 生成对象
        /// </summary>
        /// <param name="position">生成坐标</param>
        /// <param name="direction">初始面朝方向</param>
        public void Spawn(Vector3 position, Vector3 direction)
        {
            //这个函数会生成一个三维向量，其 x、y 和 z 坐标值会在一个单位半径1的球体内随机分布。
            this.position = position + UnityEngine.Random.insideUnitSphere;
            var dir = direction;
            velocity = dir * speed;
            active = true;
        }
    }

    /// <summary>
    /// 队伍数据
    /// </summary>
    [Serializable]
    public class Team
    {
        /// <summary>
        /// 绘制在 Gizmos 中显示飞船的颜色
        /// </summary>
        public Color shipColor;
        /// <summary>
        /// 激光颜色，弹幕颜色
        /// </summary>
        public Color laserColor;
        /// <summary>
        /// 队伍参数，生成点
        /// </summary>
        public Transform[] spawnPoints;
        /// <summary>
        /// 模型信息
        /// </summary>
        public Ship ship;
    }

    /// <summary>
    /// 模型信息
    /// </summary>
    [Serializable]
    public class Ship
    {
        public Mesh mesh;
    }

    #if UNITY_EDITOR

    private GUIStyle style;
    
    private void OnDrawGizmos()
    {
        foreach (var boid in _boidsAlt)
        {
            Gizmos.color = teams[boid.team].shipColor * (boid.active ? 1f : 0.1f);
            Gizmos.DrawWireSphere(boid.position, boid.id == boidIndex ? 20f : 10f);
            if (boid.active)
            {
                Gizmos.color = Color.gray * 0.25f;
                Gizmos.DrawLine(boid.position, boid.destination);
                if (boid.targetID >= 0)
                {
                    var target = _boidsAlt[boid.targetID];
                    Gizmos.color = Color.red * 0.25f;
                    Gizmos.DrawLine(boid.position, target.position);
                    Gizmos.color = Color.red;
                    Gizmos.DrawLine(boid.position,
                        (Vector3) boid.position + Vector3.Normalize(target.position - boid.position) * 25f);
                }

                var text =// $"boid:{boid.id}\n" +
                           $"HP:{boid.health}";
                style ??= new GUIStyle(); 
                style.normal.textColor = teams[boid.team].shipColor;
                Handles.Label(boid.position, text, style);
            }
        }
    }
    #endif
}
