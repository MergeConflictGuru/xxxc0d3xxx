(() => {
 const api=window.conversationAccounts;
 if(!api)return;
 // Replace prior handlers when the controller refreshes this script.
 window.__desktopFilesCleanup?.();
 const controller=new AbortController();
 window.__desktopFilesCleanup=()=>controller.abort();
 window.__desktopFilesUI=true;
 function localURL(value){
  if(!value)return null;
  if(value.startsWith('file:')||value.startsWith('app://fs/@fs/'))return value;
  // File references carry native Windows paths rather than anchor URLs.
  if(/^[A-Za-z]:[\\/]/.test(value)){
   const path=value.replace(/\\/g,'/').replace(/:\d+(?::\d+)?$/,'');
   return 'file:///'+path.split('/').map(encodeURIComponent).join('/').replace(/^([A-Za-z])%3A/,'$1:');
  }
  return null;
 }
 function targetURL(target){
  const node=target instanceof Element?target:target?.parentElement;
  const link=node?.closest('[data-prompt-link-href],a[href],[data-file-reference]');
  if(link){
   const url=localURL(link.getAttribute('data-prompt-link-href')||link.getAttribute('href')||link.getAttribute('data-markdown-copy-text'));
   if(url)return url;
  }
  const image=node?.closest('img');
  const url=localURL(image?.currentSrc||image?.src);
  if(url)return url;
  // Rendered local images may use data/blob previews; retain their original path.
  if(image){
   let fiber=image[Object.keys(image).find(key=>key.startsWith('__reactFiber$'))];
   for(let depth=0;fiber&&depth<24;depth++,fiber=fiber.return){
    const original=localURL(fiber.memoizedProps?.sourceSrc);
    if(original)return original;
   }
  }
  for(let e=node,depth=0;e&&depth<5;e=e.parentElement,depth++){
   const match=getComputedStyle(e).backgroundImage.match(/^url\(["']?(.*?)["']?\)$/);
   if(match){const url=localURL(match[1]);if(url)return url;}
  }
  return null;
 }
 function consume(event){event.preventDefault();event.stopImmediatePropagation();}
 async function reveal(url){try{await api.reveal(url)}catch(error){alert(error.message)}}
 const editorEvents=new WeakSet();
 // Window capture runs before React/document handlers that launch an editor.
 for(const type of ['pointerdown','mousedown','mouseup','pointerup','click','auxclick','dblclick']){
  window.addEventListener(type,event=>{
   if(editorEvents.has(event)||(!event.ctrlKey&&!event.shiftKey)||event.button!==0)return;
   const link=event.target.closest?.('a[href],[data-prompt-link-href]');
   const webURL=link?.getAttribute('href')||link?.getAttribute('data-prompt-link-href');
   if(event.shiftKey&&/^https?:\/\//i.test(webURL||'')){
    consume(event);
    if(type==='click')void api.openBrowser(webURL).catch(error=>alert(error.message));
    return;
   }
   const url=targetURL(event.target);if(!url)return;
   consume(event);
   if(type==='click'){
    if(event.ctrlKey)void reveal(url);
    else{
     // Delegate to the app's editor action, using its configured preferred target.
     const editorClick=new MouseEvent('click',{bubbles:true,cancelable:true,view:window,button:0,ctrlKey:true});
     editorEvents.add(editorClick);
     event.target.dispatchEvent(editorClick);
    }
   }
  },{capture:true,signal:controller.signal});
 }
 let imageMenuURL=null;
 window.addEventListener('click',event=>{
  const item=event.target.closest?.('[role="menuitem"]');
  if(imageMenuURL&&item?.textContent.trim()==='Open in Explorer'){
   consume(event);const url=imageMenuURL;imageMenuURL=null;void reveal(url);
  }
 },{capture:true,signal:controller.signal});
 window.addEventListener('contextmenu',event=>{
  imageMenuURL=null;

  const url=targetURL(event.target);if(!url)return;
  if(event.target.closest?.('img')){imageMenuURL=url;return;}
  consume(event);
  document.querySelector('#desktop-file-menu')?.remove();
  const menu=document.createElement('button');menu.id='desktop-file-menu';menu.textContent='Open in Explorer';
  menu.style.cssText=`position:fixed;z-index:2147483647;left:${Math.max(0,Math.min(event.clientX,innerWidth-170))}px;top:${Math.max(0,Math.min(event.clientY,innerHeight-40))}px;padding:8px 14px;background:#242424;color:white;border:1px solid #666;border-radius:6px`;
  menu.onclick=()=>{menu.remove();void reveal(url)};
  document.body.appendChild(menu);
  document.addEventListener('pointerdown',e=>{if(!menu.contains(e.target))menu.remove()},{once:true,capture:true,signal:controller.signal});
 },{capture:true,signal:controller.signal});
})();
