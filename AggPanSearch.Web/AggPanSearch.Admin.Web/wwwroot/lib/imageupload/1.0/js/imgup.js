
(function ($) {
    "use strict";

    var deleteParent;
    var deleteDisplay = 'none';
    var defaults = {
        fileType: ["jpg", "png", "bmp", "jpeg"],   // 上传文件的类型
        fileSize: 1024 * 1024 * 10                 // 上传文件的大小 10M
    };

    $.fn.imageUpload = function (option, param) {
        if (typeof option == 'string') {
            return $.fn.imageUpload.methods[option](this, param);
        }
        var _option = $.extend({}, $.fn.imageUpload.defaults, option || {});
        var target = $(this);
        var id = target.attr("id");
        var inputFileId = id + "_file";
        var html = '';
        html += '<section class="img-section">';
        html += '    <div class="z_photo upimg-div clear">';
        html += '       <section class="z_file fl">';
        if (_option.canAdd == 1) {
            html += '           <img src="' + _option.context + 'lib/imageupload/1.0/img/add.png" class="add-img">';
            deleteDisplay = 'block';
        }
        html += '           <input type="file" multiple name="' + inputFileId + '" id="' + inputFileId + '" class="file-image" callback="' + _option.uploadImage + '" context="' + _option.context + '" limit="' + _option.limit + '"  value="" accept="image/jpg,image/jpeg,image/png,image/bmp" />';
        html += '       </section>';
        html += '   </div>';
        html += '</section>';
        html += '<aside class="mask works-mask">';
        html += '   <div class="mask-content">';
        html += '       <div class="del-p">您确定要删除图片吗？</div>';
        html += '       <div class="check-p"><span class="del-com wsdel-ok">确定</span><span class="wsdel-no">取消</span></div>';
        html += '   </div>';
        html += '</aside>';
        target.append(html);

        target.find(".wsdel-ok").click(function () {
            $(".works-mask").hide();
            var numUp = deleteParent.siblings().length;
            if (numUp < 6) {
                deleteParent.parent().find(".z_file").show();
            }
            deleteParent.remove();
        });

        target.find(".wsdel-no").click(function () {
            $(".works-mask").hide();
        });

        $("#" + inputFileId).change(function () {
            prepareUploadImage(inputFileId);
        });

        bindImageSortable(target);
    };

    $.fn.imageUpload.defaults = {
        uploadImage: '',    // 上传图片回调
        limit: 10,          // 上传限制
        context: '',        // 当前页面根目录
        canPreview: 1,      // 是否可以预览(0不可以，1可以)
        canAdd: 1           // 是否可以添加(0不可以，1可以)
    };

    $.fn.imageUpload.methods = {
        getImageUrl: function (target) {
            var imageUrl = '';
            var list = $(target).find('.up-section').find('.up-img');
            for (var i = 0; i < list.length; i++) {
                if (i == 0) {
                    imageUrl += $(list[i]).attr("src");
                } else {
                    imageUrl += ';';
                    imageUrl += $(list[i]).attr("src");
                }
            }
            return imageUrl;
        },
        setImageUrl: function (target, imageUrl) {
            if (imageUrl) {
                var id = $(target).attr("id");
                var inputFileId = id + "_file";
                var context = $("#" + inputFileId).attr("context");

                var urlArr = imageUrl.split(';');
                for (var i = 0; i < urlArr.length; i++) {
                    if (urlArr[i] != "") {
                        var deleteId = ys.getGuid();
                        var imageSrc = urlArr[i].indexOf('http') > -1 ? urlArr[i] : context + ys.trimStart(urlArr[i], '/');
                        var html = '';
                        html += '<section class="up-section fl" draggable="true">';
                        html += '   <span class="up-span" draggable="true"></span>';

                        html += '   <img id="' + deleteId + '" class="close-upimg" draggable="false" style="display:' + deleteDisplay + '" src="' + context + 'lib/imageupload/1.0/img/delete.png" />';
                        html += '   <img class="up-img" draggable="false" src="' + imageSrc + '" />';
                        html += '   <p class="img-name-p" title="' + imageSrc + '">' + imageSrc + '</p>';
                        html += '</section>';
                        $(html).insertBefore($(target).find(".z_file"));
                        $("#" + deleteId).on("click", function () {
                            $("#" + inputFileId).imageUpload("deleteImage", deleteId)
                        });
                    }
                }
                $(".up-span").Huipreview();
                $("#" + inputFileId).imageUpload("checkImageLimit")
            }
        },
        deleteImage: function (target, deleteId) {
            var _target = $(target);
            var inputFileId = _target.attr("id");
            if (!!event) {
                event.preventDefault();
                event.stopPropagation();
            }
            $(".works-mask").show();
            deleteParent = $("#" + deleteId).parent();
            $("#" + inputFileId).imageUpload("checkImageLimit");
        },
        checkImageLimit: function (target) {
            var _target = $(target);
            var num = _target.parents(".z_photo").find(".up-section").length;
            var limit = _target.attr("limit");
            if (num >= limit) {
                _target.parent().hide();
            } else {
                _target.parent().show();
            }
        }
    };

    // ---------- 图片拖动排序 ----------
    var dragSourceSection = null;

    function bindImageSortable(target) {
        var container = $(target).find(".z_photo").first();
        if (!container.length || container.data("imageSortable")) {
            return;
        }
        container.data("imageSortable", true);

        // 鼠标按下时再标记为可拖拽，避免影响图片默认行为
        container.on("mousedown", ".up-section", function (e) {
            if ($(e.target).hasClass("close-upimg")) {
                return;
            }
            this.setAttribute("draggable", "true");
            $(this).find(".up-img, .close-upimg").attr("draggable", "false");
            $(this).find(".up-span").attr("draggable", "true");
        });

        container.on("dragstart", ".up-section", function (e) {
            var originalEvent = e.originalEvent || e;
            dragSourceSection = this;
            try {
                originalEvent.dataTransfer.effectAllowed = "move";
                originalEvent.dataTransfer.setData("text/plain", "move");
                var dragImg = this.querySelector(".up-img");
                if (dragImg && originalEvent.dataTransfer.setDragImage) {
                    originalEvent.dataTransfer.setDragImage(dragImg, 35, 35);
                }
            } catch (ex) { }
            $(this).addClass("sortable-dragging");
        });

        container.on("dragend", ".up-section", function () {
            $(this).removeClass("sortable-dragging");
            dragSourceSection = null;
        });

        // 拖动经过某张图片时，按鼠标位于目标的左/右半边决定插到前面还是后面
        container.on("dragover", ".up-section", function (e) {
            var originalEvent = e.originalEvent || e;
            if (!dragSourceSection || dragSourceSection === this) {
                return;
            }
            originalEvent.preventDefault();
            var rect = this.getBoundingClientRect();
            // 整行链接样式按纵向判断前后，方形缩略图按横向判断
            var isAfter = rect.width > rect.height * 2
                ? originalEvent.clientY > rect.top + rect.height / 2
                : originalEvent.clientX > rect.left + rect.width / 2;
            var $current = $(this);
            if (isAfter) {
                if ($current.next()[0] !== dragSourceSection) {
                    $current.after(dragSourceSection);
                }
            } else if ($current.prev()[0] !== dragSourceSection) {
                $current.before(dragSourceSection);
            }
        });

        // 拖到“添加图片”按钮上时排到最后
        container.on("dragover", ".z_file", function (e) {
            if (!dragSourceSection) {
                return;
            }
            (e.originalEvent || e).preventDefault();
            var $zFile = $(this);
            if ($zFile.prev()[0] !== dragSourceSection) {
                $zFile.before(dragSourceSection);
            }
        });

        // 允许在容器空白处释放
        container.on("dragover", function (e) {
            if (dragSourceSection) {
                (e.originalEvent || e).preventDefault();
            }
        });

        container.on("drop", function (e) {
            (e.originalEvent || e).preventDefault();
        });
    }

    function prepareUploadImage(inputFileId) {
        var target = $("#" + inputFileId);
        var callback = target.attr("callback");
        var limit = target.attr("limit");
        if (!callback || callback == '') {
            alert("请提供上传方法");
            return;
        }
        var inputFile = document.getElementById(inputFileId);
        var imgContainer = target.parents(".z_photo"); //存放图片的父亲元素
        var fileList = inputFile.files; //获取的图片文件
        //遍历得到的图片文件
        var num = imgContainer.find(".up-section").length;
        var totalNum = num + fileList.length;  //总的数量
        if (fileList.length > limit || totalNum > limit) {
            alert("上传图片数目不可以超过" + limit + "个，请重新选择");  //一次选择上传超过5个 或者是已经上传和这次上传的到的总数也不可以超过5个
        }
        else if (num < limit) {

            validateUpload(fileList);

            var uploadSuccess = function (imgPath) {
                var section = $("<section class='up-section fl loading' draggable='true'>");
                imgContainer.find(".z_file").before(section);
                var span = $("<span class='up-span' draggable='true'>");
                span.appendTo(section);

                var context = $("#" + inputFileId).attr("context");
                var deleteId = ys.getGuid();
                var deleteImg = $("<img id='" + deleteId + "' class='close-upimg' draggable='false' style='display:" + deleteDisplay + "' />").on("click", function () {
                    $("#" + inputFileId).imageUpload("deleteImage", deleteId)
                });
                deleteImg.attr("src", context + "lib/imageupload/1.0/img/delete.png").appendTo(section);

                var realImg = $("<img class='up-img up-opcity' draggable='false'>");
                realImg.attr("src", imgPath);
                realImg.appendTo(section);
                var p = $("<p class='img-name-p'>");
                p.attr("title", imgPath).text(imgPath).appendTo(section);

                setTimeout(function () {
                    $(".up-section").removeClass("loading");
                    $(".up-img").removeClass("up-opcity");
                }, 450);

                $("#" + inputFileId).imageUpload("checkImageLimit");
            };
            for (var i = 0; i < fileList.length; i++) {
                doCallback(eval(callback), [fileList[i], uploadSuccess]);
            }
        }
    }

    function validateUpload(files) {
        var arrFiles = [];//替换的文件数组
        for (var i = 0, file; file = files[i]; i++) {
            //获取文件上传的后缀名
            var newStr = file.name.split("").reverse().join("");
            if (newStr.split(".")[0] != null) {
                var type = newStr.split(".")[0].split("").reverse().join("");
                console.log(type + "===type===");
                if (jQuery.inArray(type, defaults.fileType) > -1) {
                    // 类型符合，可以上传
                    if (file.size >= defaults.fileSize) {
                        alert(file.size);
                        alert('您这个"' + file.name + '"文件大小过大');
                    } else {
                        // 在这里需要判断当前所有文件中
                        arrFiles.push(file);
                    }
                } else {
                    alert('您这个"' + file.name + '"上传类型不符合');
                }
            } else {
                alert('您这个"' + file.name + '"没有类型, 无法识别');
            }
        }
        return arrFiles;
    }

    // 动态调用方法，并传递参数
    function doCallback(fn, args) {
        fn.apply(this, args);
    }
})(jQuery);

