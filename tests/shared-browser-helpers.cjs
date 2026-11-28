'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');

function folderFixtures(data,large=false) {
  const shared=path.join(data,'shared'),sample=path.join(shared,'演示子目录');
  fs.mkdirSync(sample,{recursive:true});fs.mkdirSync(path.join(shared,'空目录'),{recursive:true});
  fs.writeFileSync(path.join(sample,'目录说明.txt'),'双击进入文件夹测试内容','utf8');
  if(large) {
    const directory=path.join(shared,'大量目录');fs.mkdirSync(directory,{recursive:true});
    for(let index=0;index<1105;index++) {
      const file=path.join(directory,`item-${String(index).padStart(4,'0')}.txt`);fs.writeFileSync(file,Buffer.alloc(index%17,index%256));
      const modified=index===0?new Date('1960-01-01T00:00:00Z'):new Date(Date.UTC(2026,0,1)+index*1000);fs.utimesSync(file,modified,modified);
    }
  }
}

function zipContents(bytes) {
  assert.ok(bytes.length>=22&&bytes.readUInt32LE(bytes.length-22)===0x06054b50,'下载的 ZIP 没有完整尾记录');
  const files=new Map();let offset=0;
  while(bytes.readUInt32LE(offset)===0x04034b50) {
    assert.equal(bytes.readUInt16LE(offset+6),0x0800,'ZIP 不是 UTF-8 或含 Data Descriptor');
    assert.equal(bytes.readUInt16LE(offset+8),0,'ZIP 未使用 Store');
    const size=bytes.readUInt32LE(offset+18),nameLength=bytes.readUInt16LE(offset+26),extraLength=bytes.readUInt16LE(offset+28);
    const name=bytes.toString('utf8',offset+30,offset+30+nameLength),start=offset+30+nameLength+extraLength;
    assert.ok(start+size<=bytes.length,'ZIP 文件内容被截断');assert.ok(!files.has(name),'ZIP 有重复名称');
    files.set(name,bytes.subarray(start,start+size));offset=start+size;
  }
  assert.equal(bytes.readUInt32LE(offset),0x02014b50,'ZIP 中央目录边界错误');return files;
}

async function waitForListing(page) {
  await page.waitForFunction(()=>document.querySelector('#file-list').getAttribute('aria-busy')==='false');
}

async function textContrast(page,selectors) {
  return page.evaluate(selectors=>{
    const canvas=document.createElement('canvas');canvas.width=canvas.height=1;const context=canvas.getContext('2d',{willReadFrequently:true});
    const color=value=>{context.clearRect(0,0,1,1);context.fillStyle=value;context.fillRect(0,0,1,1);const pixels=context.getImageData(0,0,1,1).data;return [pixels[0],pixels[1],pixels[2],pixels[3]/255];};
    const blend=(front,back)=>[0,1,2].map(index=>front[index]*front[3]+back[index]*(1-front[3]));
    const luminance=value=>value.map(channel=>{const normalized=channel/255;return normalized<=.04045?normalized/12.92:Math.pow((normalized+.055)/1.055,2.4);}).reduce((sum,value,index)=>sum+value*[.2126,.7152,.0722][index],0);
    return [...document.querySelectorAll(selectors)].filter(element=>element.getBoundingClientRect().width>0&&element.getBoundingClientRect().height>0).map(element=>{
      let background=[255,255,255];const parents=[];for(let parent=element;parent;parent=parent.parentElement)parents.unshift(parent);
      for(const parent of parents)background=blend(color(getComputedStyle(parent).backgroundColor),background);
      const foreground=blend(color(getComputedStyle(element).color),background),first=luminance(foreground),second=luminance(background);
      return {text:element.textContent||element.getAttribute('aria-label'),ratio:(Math.max(first,second)+.05)/(Math.min(first,second)+.05)};
    });
  },selectors);
}

module.exports={folderFixtures,zipContents,waitForListing,textContrast};
