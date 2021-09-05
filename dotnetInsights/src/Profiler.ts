////////////////////////////////////////////////////////////////////////////////
// Module: Profiler.ts
////////////////////////////////////////////////////////////////////////////////

import { time } from "console";

export class ProfilerEvent {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private methodName: string;
    private methodId: number;
    private timeInMethod: number;
    private timestamp: number;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(methodName: string, methodId: number, timeInMethod: number) {
        this.methodName = methodName;
        this.methodId = methodId;
        this.timeInMethod = timeInMethod;
        this.timestamp = Date.now();
    }
}

export class Profiler {
    ////////////////////////////////////////////////////////////////////////////
    // Member variables
    ////////////////////////////////////////////////////////////////////////////

    private instance: Profiler | undefined | null;
    private pid: number;
    private data: ProfilerEvent[];

    private methodMap: Map<string, Map<number, number>>;

    ////////////////////////////////////////////////////////////////////////////
    // Constructor
    ////////////////////////////////////////////////////////////////////////////

    constructor(pid: number) {
        this.pid = pid;

        this.data = [] as ProfilerEvent[];
        this.methodMap = new Map<string, Map<number, number>>();
    }

    ////////////////////////////////////////////////////////////////////////////
    // Member methods
    ////////////////////////////////////////////////////////////////////////////

    public addData(jsonData: any) {
        const methodName = jsonData["methodName"];
        const methodId = jsonData["id"];
        const timeInMethod = jsonData["time"];

        const profilerEvent = new ProfilerEvent(methodName, methodId, timeInMethod);
        this.data.push(profilerEvent);

        var methodByProfilerFunctionId: Map<number, number> | undefined = undefined;
        if (this.methodMap.has(methodName))
        {
            methodByProfilerFunctionId = this.methodMap.get(methodName);
        }
        else {
            methodByProfilerFunctionId = new Map<number, number>();
        }

        if (methodByProfilerFunctionId?.has(methodId)) {
            const previousValue = methodByProfilerFunctionId.get(methodId);
            methodByProfilerFunctionId.set(methodId, previousValue + timeInMethod);
        }
        else {
            methodByProfilerFunctionId?.set(methodId, timeInMethod);
        }
    }

    public getInstance(pid: number): Profiler {
        if (this.instance == null || this.instance == undefined || this.instance.pid !== pid) {
            this.instance = new Profiler(pid);
        }

        return this.instance;
    }
}