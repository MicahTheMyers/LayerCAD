"use strict";

function extrudeProfile(profile, height) {
    if (profile.length < 3 || !Number.isFinite(height) || height <= 0 || height > 10000)
        throw new Error("Place at least three points and enter a height between 0 and 10,000 mm.");
    const cross = (a,b,c) => (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
    const area = profile.reduce((sum, a, i) => {
        const b = profile[(i+1)%profile.length];
        return sum + a[0]*b[1]-b[0]*a[1];
    }, 0);
    const invalid = () => new Error("Draw a simple outline with no crossing edges or overlapping points.");
    if (!area || new Set(profile.map(p => p.join(","))).size !== profile.length) throw invalid();
    const loop = area > 0 ? profile : [...profile].reverse();
    for (let i=0; i<loop.length; i++) {
        const a=loop[i], b=loop[(i+1)%loop.length];
        for (let j=i+2; j<loop.length; j++) {
            if (i===0 && j===loop.length-1) continue;
            const c=loop[j], d=loop[(j+1)%loop.length];
            const overlaps = [0,1].every(k => Math.max(Math.min(a[k],b[k]),Math.min(c[k],d[k])) <=
                Math.min(Math.max(a[k],b[k]),Math.max(c[k],d[k])));
            if (overlaps && cross(a,b,c)*cross(a,b,d)<=0 && cross(c,d,a)*cross(c,d,b)<=0) throw invalid();
        }
    }
    const base=loop.map(([x,y])=>[x,y,0]), top=loop.map(([x,y])=>[x,y,height]);
    const remaining=loop.map((_,i)=>i), triangles=[];
    function cap(a,b,c) {
        triangles.push([base[c],base[b],base[a]],[top[a],top[b],top[c]]);
    }
    while (remaining.length>3) {
        const ear=remaining.findIndex((b,i)=>{
            const a=remaining.at(i-1), c=remaining[(i+1)%remaining.length];
            return cross(loop[a],loop[b],loop[c])>0 && !remaining.some(p=>p!==a && p!==b && p!==c &&
                cross(loop[a],loop[b],loop[p])>=0 && cross(loop[b],loop[c],loop[p])>=0 &&
                cross(loop[c],loop[a],loop[p])>=0);
        });
        if (ear<0) throw invalid();
        cap(remaining.at(ear-1),remaining[ear],remaining[(ear+1)%remaining.length]);
        remaining.splice(ear,1);
    }
    if (cross(...remaining.map(i=>loop[i]))<=0) throw invalid();
    cap(...remaining);
    for (let a=0;a<loop.length;a++) {
        const b=(a+1)%loop.length;
        triangles.push([base[a],base[b],top[b]],[base[a],top[b],top[a]]);
    }
    return triangles;
}

function binaryStl(triangles) {
    const bytes=new ArrayBuffer(84+50*triangles.length);
    const view=new DataView(bytes);
    view.setUint32(80,triangles.length,true);
    triangles.forEach(([a,b,c],i)=>{
        const [x,y,z]=b.map((value,j)=>value-a[j]), [u,v,w]=c.map((value,j)=>value-a[j]);
        const normal=[y*w-z*v,z*u-x*w,x*v-y*u];
        const length=Math.hypot(...normal)||1;
        [...normal.map(value=>value/length),...a,...b,...c].forEach((value,j)=>view.setFloat32(84+i*50+j*4,value,true));
    });
    return bytes;
}

if (typeof module !== "undefined") module.exports={extrudeProfile,binaryStl};
