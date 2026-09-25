from __future__ import annotations
import datetime
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union
from uuid import UUID

if TYPE_CHECKING:
    from .processing_status import ProcessingStatus

@dataclass
class FileVersionResponse(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The createdAt property
    created_at: Optional[datetime.datetime] = None
    # The createdBy property
    created_by: Optional[UUID] = None
    # The fileName property
    file_name: Optional[str] = None
    # The isCurrent property
    is_current: Optional[bool] = None
    # The languages property
    languages: Optional[str] = None
    # The mediaType property
    media_type: Optional[str] = None
    # The number property
    number: Optional[int] = None
    # The operationId property
    operation_id: Optional[UUID] = None
    # The pageCount property
    page_count: Optional[int] = None
    # The processingError property
    processing_error: Optional[str] = None
    # Processing of a file version (DOC-09): text extraction, OCR, thumbnails.
    processing_status: Optional[ProcessingStatus] = None
    # The sha256 property
    sha256: Optional[str] = None
    # The size property
    size: Optional[int] = None
    # The source property
    source: Optional[str] = None
    # The textLanguage property
    text_language: Optional[str] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> FileVersionResponse:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: FileVersionResponse
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return FileVersionResponse()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .processing_status import ProcessingStatus

        from .processing_status import ProcessingStatus

        fields: dict[str, Callable[[Any], None]] = {
            "createdAt": lambda n : setattr(self, 'created_at', n.get_datetime_value()),
            "createdBy": lambda n : setattr(self, 'created_by', n.get_uuid_value()),
            "fileName": lambda n : setattr(self, 'file_name', n.get_str_value()),
            "isCurrent": lambda n : setattr(self, 'is_current', n.get_bool_value()),
            "languages": lambda n : setattr(self, 'languages', n.get_str_value()),
            "mediaType": lambda n : setattr(self, 'media_type', n.get_str_value()),
            "number": lambda n : setattr(self, 'number', n.get_int_value()),
            "operationId": lambda n : setattr(self, 'operation_id', n.get_uuid_value()),
            "pageCount": lambda n : setattr(self, 'page_count', n.get_int_value()),
            "processingError": lambda n : setattr(self, 'processing_error', n.get_str_value()),
            "processingStatus": lambda n : setattr(self, 'processing_status', n.get_enum_value(ProcessingStatus)),
            "sha256": lambda n : setattr(self, 'sha256', n.get_str_value()),
            "size": lambda n : setattr(self, 'size', n.get_int_value()),
            "source": lambda n : setattr(self, 'source', n.get_str_value()),
            "textLanguage": lambda n : setattr(self, 'text_language', n.get_str_value()),
        }
        return fields
    
    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_datetime_value("createdAt", self.created_at)
        writer.write_uuid_value("createdBy", self.created_by)
        writer.write_str_value("fileName", self.file_name)
        writer.write_bool_value("isCurrent", self.is_current)
        writer.write_str_value("languages", self.languages)
        writer.write_str_value("mediaType", self.media_type)
        writer.write_int_value("number", self.number)
        writer.write_uuid_value("operationId", self.operation_id)
        writer.write_int_value("pageCount", self.page_count)
        writer.write_str_value("processingError", self.processing_error)
        writer.write_enum_value("processingStatus", self.processing_status)
        writer.write_str_value("sha256", self.sha256)
        writer.write_int_value("size", self.size)
        writer.write_str_value("source", self.source)
        writer.write_str_value("textLanguage", self.text_language)
        writer.write_additional_data_value(self.additional_data)
    

