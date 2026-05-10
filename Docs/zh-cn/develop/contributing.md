# MAAUnified 贡献说明

`MAAUnified` 挂载在 `MaaAssistantArknights/src/MAAUnified`，以 submodule 形式接入主仓。因此，对 `MAAUnified` 的修改不能只停留在子仓内；子仓改动合并后，还需要再更新主仓中的 submodule 指针，主仓的 CI、联调和打包结果才会使用新的提交。

下面是推荐的贡献流程。

## 推荐流程

推荐按下面的顺序参与 `MAAUnified` 开发：

1. fork `MaaAssistantArknights` 主仓和 `MaaUnified` 子仓。
2. 从自己的主仓 fork 克隆 `dev-v2`，并初始化 `src/MAAUnified` 与 `src/MaaUtils`。
3. 将 `src/MAAUnified` 的官方仓库配置为 `upstream`，自己的 `MaaUnified` fork 配置为 `origin`。
4. 在 `src/MAAUnified` 中基于 `upstream/dev` 切出工作分支。
5. 在主仓环境中联调、测试、提交，并将工作分支推送到自己的 `MaaUnified` fork。
6. 向官方 `MaaUnified` 的 `dev` 分支发起 PR。
7. 由维护者合并子仓 PR，推进 `dev -> main`，再更新主仓 `dev-v2` 中的 `src/MAAUnified` 指针。

这套流程里有两套分支，各自负责的事情不同：

- `MaaUnified` 子仓使用 `dev` 作为日常集成分支，`main` 作为供主仓稳定引用的分支；
- `MaaAssistantArknights` 主仓当前使用 `dev-v2` 作为日常协作分支。

普通贡献者默认只需要完成子仓 PR。主仓 submodule 指针更新默认由 `MAAUnified` 维护者或主仓维护者处理；只有在本次改动同时涉及主仓配套内容，或者维护者明确要求时，才由贡献者一并准备主仓 PR。

## 详细步骤

### 1. 准备仓库

推荐同时 fork 下面两个仓库：

- `MaaAssistantArknights/MaaAssistantArknights`
- `MaaAssistantArknights/MaaUnified`

随后从自己的主仓 fork 克隆 `dev-v2`：

```bash
git clone --recurse-submodules <你的 MaaAssistantArknights fork> -b dev-v2 --single-branch
cd MaaAssistantArknights
git submodule sync --recursive
git submodule update --init --depth 1 src/MAAUnified src/MaaUtils
```

为主仓配置官方远端：

```bash
git remote add upstream https://github.com/MaaAssistantArknights/MaaAssistantArknights.git
git fetch upstream
```

为 `src/MAAUnified` 配置远端。推荐使用下面这组命名：

```bash
git -C src/MAAUnified remote rename origin upstream
git -C src/MAAUnified remote add origin <你的 MaaUnified fork>
git -C src/MAAUnified remote -v
```

此后约定如下：

- 主仓：`origin` 为你的 `MaaAssistantArknights` fork，`upstream` 为官方主仓；
- 子仓：`origin` 为你的 `MaaUnified` fork，`upstream` 为官方 `MaaUnified`。

### 2. 从子仓 `dev` 切工作分支

`MAAUnified` 的日常集成分支是 `dev`。开始修改前，先在子仓同步官方 `dev`，再从这里切工作分支：

```bash
git -C src/MAAUnified fetch upstream
git -C src/MAAUnified switch dev || git -C src/MAAUnified switch -c dev --track upstream/dev
git -C src/MAAUnified pull --ff-only upstream dev
git -C src/MAAUnified switch -c feat/<topic>
```

分支名只要能表达改动主题即可，例如：

- `feat/<topic>`
- `fix/<topic>`
- `docs/<topic>`

不要直接在 `dev` 上开发，也不要直接向官方 `MaaUnified/dev` 推送提交。

### 3. 在主仓环境中联调

`MAAUnified` 不是一个只靠 `dotnet run` 就能完整验证的独立 UI 工程。开发时应始终在 `MaaAssistantArknights` 主仓环境中联调，连同下面这些内容一起检查：

- MaaCore runtime；
- `resource/` 资源目录；
- 平台启动入口和打包布局；
- `MAAUnified` 自身测试及平台集成行为。

本地运行、构建和测试命令见 [本地开发](./development.md)。

### 4. 提交子仓改动

改动完成后，在子仓内提交并推送工作分支：

```bash
git -C src/MAAUnified status --short
git -C src/MAAUnified add <files>
git -C src/MAAUnified commit -m "feat(<area>): <summary>"
git -C src/MAAUnified push -u origin feat/<topic>
```

这里的 `origin` 应当是你自己的 `MaaUnified` fork。

### 5. 向官方 `MaaUnified/dev` 发起 PR

在 GitHub 上发起 Pull Request：

- 源分支：你的 `MaaUnified` fork 中的工作分支；
- 目标分支：官方 `MaaUnified` 的 `dev`。

到这一步，普通贡献者在子仓侧的工作就完成了。接下来是维护者评审、合并，以及后续的主仓集成。

### 6. 维护者更新主仓指针

子仓 PR 合并后，主仓不会自动跟进。只有把主仓里的 `src/MAAUnified` 指针更新到新的子仓提交，主仓才会真正使用这次改动。

这一步默认由维护者完成。推荐顺序如下：

1. 合并 `MaaUnified/dev` 上的 PR；
2. 将子仓 `dev` 推进到 `main`；
3. 回到主仓 `dev-v2`，更新 `src/MAAUnified` 指针；
4. 发起主仓 PR，并继续后续 CI、联调与发布流程。

以下命令从 `MaaAssistantArknights` 主仓根目录执行：

```bash
git fetch upstream
git switch dev-v2 || git switch -c dev-v2 --track upstream/dev-v2
git pull --ff-only upstream dev-v2
git switch -c chore/bump-maaunified

git -C src/MAAUnified fetch origin
git -C src/MAAUnified switch main || git -C src/MAAUnified switch -c main --track origin/main
git -C src/MAAUnified pull --ff-only origin main

git status --short
git diff --submodule
git add src/MAAUnified
git commit -m "chore: bump src/MAAUnified"
git push -u origin chore/bump-maaunified
```

随后向官方 `MaaAssistantArknights/dev-v2` 发起主仓 PR。

`git add src/MAAUnified` 提交的是主仓中的 submodule 指针变化，不是重新提交一份子仓文件。

### 7. 同时涉及主仓和子仓时的顺序

如果一次改动同时涉及：

- `src/MAAUnified` 子仓代码、文档或测试；
- 主仓中的 workflow、打包脚本、联调逻辑或文档；

推荐顺序仍然不变：

1. 先完成子仓 PR；
2. 再更新主仓 `src/MAAUnified` 指针；
3. 最后把主仓配套改动与指针更新一起提交到 `dev-v2`。

不要反过来先提交主仓指针，再回头补子仓归宿。主仓应当引用已经进入官方子仓主线的提交。

### 8. 常见误区

- 在 `src/MAAUnified` 里改完就结束，没有给 `MaaUnified` 子仓提交 PR；
- 直接向官方 `MaaUnified/dev` 推送提交；
- 只验证托管前端，不验证主仓运行环境；
- 子仓 PR 已经合并，但忘记主仓还需要更新 `src/MAAUnified` 指针；
- 把 `MaaUnified` 的 `dev/main` 和主仓的 `dev-v2/master-v2` 当成同一套分支来理解。
